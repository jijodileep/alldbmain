using MySqlManager.Application;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Configuration;
using MySqlManager.Infrastructure;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Quartz;
using Serilog;

namespace MySqlManager.Worker;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        Log.Logger = new LoggerConfiguration().WriteTo.Console().WriteTo.File("logs/worker-.log", rollingInterval: RollingInterval.Day).CreateLogger();
        builder.Services.AddSerilog(Log.Logger);
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddQuartz(quartz =>
        {
            var key = new JobKey("enqueue-backups");
            quartz.AddJob<EnqueueBackupsQuartzJob>(options => options.WithIdentity(key));
            quartz.AddTrigger(trigger => trigger.ForJob(key).WithIdentity("enqueue-backups-trigger").WithCronSchedule("0 * * * * ?"));
        });
        builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
        builder.Services.AddHostedService<BackupWorker>();
        builder.Services.AddHostedService<MigrationWorker>();
        builder.Services.AddHostedService<RestoreWorker>();
        builder.Services.AddHostedService<HealthCheckWorker>();
        builder.Services.AddHostedService<NotificationWorker>();
        var telemetry = builder.Configuration.GetSection(TelemetryOptions.Section).Get<TelemetryOptions>() ?? new TelemetryOptions();
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("MySqlManager.Worker"))
            .WithTracing(tracing =>
            {
                tracing.AddSource("MySqlManager");
                if (!string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint)) tracing.AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.OtlpEndpoint));
            })
            .WithMetrics(metrics => metrics.AddMeter("MySqlManager"));
        var host = builder.Build();
        await host.RunAsync();
    }
}

public sealed class EnqueueBackupsQuartzJob : IJob
{
    private readonly IServiceScopeFactory _scopes;
    public EnqueueBackupsQuartzJob(IServiceScopeFactory scopes) => _scopes = scopes;
    public async Task Execute(IJobExecutionContext context)
    {
        using var scope = _scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IBackupScheduleEnqueuer>().EnqueueDueAsync(context.CancellationToken);
    }
}

public abstract class JobWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly int _max;
    private readonly Func<IServiceProvider, IJobQueue> _queue;
    private readonly Func<IJobQueue, string, TimeSpan, CancellationToken, Task<Guid?>> _lease;
    private readonly Func<IServiceProvider, Guid, CancellationToken, Task> _execute;

    protected JobWorker(IServiceScopeFactory scopes, int max, Func<IServiceProvider, IJobQueue> queue, Func<IJobQueue, string, TimeSpan, CancellationToken, Task<Guid?>> lease, Func<IServiceProvider, Guid, CancellationToken, Task> execute)
    {
        _scopes = scopes;
        _max = Math.Max(1, max);
        _queue = queue;
        _lease = lease;
        _execute = execute;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var gate = new SemaphoreSlim(_max);
        while (!stoppingToken.IsCancellationRequested)
        {
            Guid? jobId = null;
            TimeSpan lease = TimeSpan.FromMinutes(10);
            try
            {
                using var scope = _scopes.CreateScope();
                var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<JobOptions>>().Value;
                lease = TimeSpan.FromSeconds(options.LeaseSeconds);
                jobId = await _lease(_queue(scope.ServiceProvider), Environment.MachineName, lease, stoppingToken);
                if (jobId is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            await gate.WaitAsync(stoppingToken);
            var captured = jobId.Value;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    await _execute(scope.ServiceProvider, captured, stoppingToken);
                }
                finally
                {
                    gate.Release();
                }
            }, stoppingToken);
        }
    }
}

public sealed class BackupWorker : JobWorker
{
    public BackupWorker(IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<JobOptions> options)
        : base(scopes, options.Value.MaxConcurrentBackups, sp => sp.GetRequiredService<IJobQueue>(), (queue, worker, lease, token) => queue.TryLeaseBackupAsync(worker, lease, token), (sp, id, token) => sp.GetRequiredService<IBackupPipeline>().ExecuteAsync(id, token))
    {
    }
}

public sealed class MigrationWorker : JobWorker
{
    public MigrationWorker(IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<JobOptions> options)
        : base(scopes, options.Value.MaxConcurrentMigrations, sp => sp.GetRequiredService<IJobQueue>(), (queue, worker, lease, token) => queue.TryLeaseMigrationAsync(worker, lease, token), (sp, id, token) => sp.GetRequiredService<ICopyPipeline>().ExecuteAsync(id, token))
    {
    }
}

public sealed class RestoreWorker : JobWorker
{
    public RestoreWorker(IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<JobOptions> options)
        : base(scopes, 1, sp => sp.GetRequiredService<IJobQueue>(), (queue, worker, lease, token) => queue.TryLeaseRestoreAsync(worker, lease, token), (sp, id, token) => sp.GetRequiredService<IRestorePipeline>().ExecuteAsync(id, token))
    {
    }
}

public sealed class HealthCheckWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    public HealthCheckWorker(IServiceScopeFactory scopes) => _scopes = scopes;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IHealthProbe>().ProbeAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch { }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}

public sealed class NotificationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    public NotificationWorker(IServiceScopeFactory scopes) => _scopes = scopes;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<INotificationDispatcher>().DispatchPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch { }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
