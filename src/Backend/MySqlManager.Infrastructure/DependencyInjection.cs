using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Configuration;
using MySqlManager.Domain.Enums;
using MySqlManager.Infrastructure.Git;
using MySqlManager.Infrastructure.MySql;
using MySqlManager.Infrastructure.Operations;
using MySqlManager.Infrastructure.Persistence;
using MySqlManager.Infrastructure.Pipelines;
using MySqlManager.Infrastructure.Security;
using MySqlManager.Infrastructure.Storage;
using StackExchange.Redis;

namespace MySqlManager.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, bool useMySql = true)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));
        services.Configure<BackupOptions>(configuration.GetSection(BackupOptions.Section));
        services.Configure<GitOptions>(configuration.GetSection(GitOptions.Section));
        services.Configure<JobOptions>(configuration.GetSection(JobOptions.Section));
        services.Configure<SeedOptions>(configuration.GetSection(SeedOptions.Section));
        services.Configure<AllowedCorsOptions>(configuration.GetSection(AllowedCorsOptions.Section));
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.Section));
        services.Configure<SecretOptions>(configuration.GetSection(SecretOptions.Section));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.Configure<RedisOptions>(configuration.GetSection(RedisOptions.Section));
        services.Configure<TelemetryOptions>(configuration.GetSection(TelemetryOptions.Section));

        if (useMySql)
        {
            var connection = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
            services.AddDbContext<AppDbContext>(options =>
                options.UseMySql(connection, new MySqlServerVersion(new Version(8, 0, 36))));
        }

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<ICurrentUser, WorkerCurrentUser>();
        services.AddScoped<IPasswordHasher, PasswordHashService>();
        services.AddScoped<ITokenService, JwtTokenService>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<ISecretProtector, AesSecretProtector>();
        services.AddScoped<IExternalSecretProvider, EnvironmentSecretProvider>();
        services.AddScoped<IExternalSecretProvider, AzureKeyVaultSecretProvider>();
        services.AddScoped<IExternalSecretProvider, AwsSecretsManagerProvider>();
        services.AddScoped<IEncryptionService, ChunkedEncryptionService>();
        services.AddScoped<Application.Security.IKeyRotation>(sp => (ChunkedEncryptionService)sp.GetRequiredService<IEncryptionService>());
        services.AddScoped<ISourceMySqlGateway, SourceMySqlGateway>();
        services.AddScoped<IDestinationMySqlGateway, DestinationMySqlGateway>();
        services.AddScoped<IMySqlBackupService, MySqlDumpBackupService>();
        services.AddScoped<IMySqlImportService, MySqlImportService>();
        services.AddSingleton<ICompressionService, GzipCompressionService>();
        services.AddSingleton<LocalFileStorage>();
        services.AddSingleton<S3BackupStorage>();
        services.AddSingleton<AzureBlobBackupStorage>();
        services.AddSingleton<IBackupStorage>(sp =>
        {
            var type = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value.Type;
            return type switch
            {
                StorageProviderKind.S3 => sp.GetRequiredService<S3BackupStorage>(),
                StorageProviderKind.AzureBlob => sp.GetRequiredService<AzureBlobBackupStorage>(),
                _ => sp.GetRequiredService<LocalFileStorage>()
            };
        });
        services.AddScoped<IGitService, GitCliService>();
        services.AddScoped<IJobProgressPublisher, JobProgressPublisher>();
        services.AddScoped<INotificationDispatcher, NotificationDispatcher>();
        services.AddScoped<IJobQueue, JobQueue>();
        services.AddScoped<IBackupPipeline, BackupPipeline>();
        services.AddScoped<ICopyPipeline, CopyPipeline>();
        services.AddScoped<IRestorePipeline, RestorePipeline>();
        services.AddScoped<IHealthProbe, HealthProbe>();
        services.AddScoped<IRetentionService, RetentionService>();
        services.AddScoped<IBackupScheduleEnqueuer, BackupScheduleEnqueuer>();

        var redis = configuration.GetSection(RedisOptions.Section).Get<RedisOptions>() ?? new RedisOptions();
        if (redis.Enabled && !string.IsNullOrWhiteSpace(redis.ConnectionString))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis.ConnectionString));
            services.AddSingleton<IDistributedLock, RedisDistributedLock>();
        }
        else
        {
            services.AddScoped<IDistributedLock, DatabaseDistributedLock>();
        }

        return services;
    }
}

public sealed class WorkerCurrentUser : ICurrentUser
{
    public Guid? UserId => null;
    public string IpAddress => "worker";
}
