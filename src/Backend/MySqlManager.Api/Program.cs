using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MySqlManager.Api.Hubs;
using MySqlManager.Api.Middleware;
using MySqlManager.Application;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Common;
using MySqlManager.Application.Configuration;
using MySqlManager.Infrastructure;
using MySqlManager.Infrastructure.Persistence;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using System.Text;

namespace MySqlManager.Api;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseSerilog((context, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File("logs/api-.log", rollingInterval: RollingInterval.Day));

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
        builder.Services.AddSignalR();
        builder.Services.AddHostedService<JobProgressRelay>();
        builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var errors = context.ModelState.Where(entry => entry.Value?.Errors.Count > 0)
                    .SelectMany(entry => entry.Value!.Errors.Select(error => new ApiFieldError(entry.Key, error.ErrorMessage)))
                    .ToList();
                return new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(ApiResponse<object>.Fail("VALIDATION_ERROR", "Validation failed", errors));
            };
        });

        var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs/jobs"))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });
        builder.Services.AddAuthorization(options =>
        {
            foreach (var permission in Permissions.All)
            {
                options.AddPolicy(permission.Code, policy => policy.RequireClaim("permission", permission.Code));
            }
        });

        var origins = builder.Configuration.GetSection(AllowedCorsOptions.Section).Get<AllowedCorsOptions>()?.Origins ?? ["http://localhost:5173"];
        builder.Services.AddCors(options => options.AddPolicy("app", policy =>
            policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "MySQL Manager API",
                Version = "v1",
                Description = "Backup, copy, clone, and restore APIs. Source databases are read-only. Long-running work is queued."
            });
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "JWT access token"
            });
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
            });
            var xml = Path.Combine(AppContext.BaseDirectory, "MySqlManager.Api.xml");
            if (File.Exists(xml))
            {
                options.IncludeXmlComments(xml);
            }
        });

        var telemetry = builder.Configuration.GetSection(TelemetryOptions.Section).Get<TelemetryOptions>() ?? new TelemetryOptions();
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("MySqlManager.Api"))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddEntityFrameworkCoreInstrumentation();
                if (!string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint)) tracing.AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.OtlpEndpoint));
                else if (builder.Environment.IsDevelopment()) tracing.AddConsoleExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation().AddMeter("MySqlManager");
                if (!string.IsNullOrWhiteSpace(telemetry.OtlpEndpoint)) metrics.AddOtlpExporter(o => o.Endpoint = new Uri(telemetry.OtlpEndpoint));
            });

        var app = builder.Build();
        app.UseMiddleware<ExceptionMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseSerilogRequestLogging();
        if (!app.Environment.IsEnvironment("Testing"))
        {
            app.UseHttpsRedirection();
        }
        app.UseCors("app");
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseSwagger();
        app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "MySQL Manager API"));
        app.MapControllers();
        app.MapHub<JobProgressHub>("/hubs/jobs");
        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        if (!app.Environment.IsEnvironment("Testing"))
        {
            await DevelopmentDataSeeder.SeedAsync(app.Services, app.Environment.IsDevelopment());
        }
        await app.RunAsync();
    }
}

public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;
    public HttpCurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;
    public Guid? UserId => Guid.TryParse(_accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public string IpAddress => _accessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

public sealed class JobProgressRelay : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHubContext<JobProgressHub> _hub;

    public JobProgressRelay(IServiceScopeFactory scopes, IHubContext<JobProgressHub> hub)
    {
        _scopes = scopes;
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
                var events = await db.JobProgressEvents.Where(e => !e.Delivered).OrderBy(e => e.CreatedAtUtc).Take(100).ToListAsync(stoppingToken);
                foreach (var progress in events)
                {
                    await _hub.Clients.Group($"job:{progress.JobId}").SendAsync("progress", new
                    {
                        progress.JobId,
                        progress.JobType,
                        progress.Step,
                        progress.State,
                        progress.Percentage,
                        progress.Message,
                        progress.CreatedAtUtc
                    }, stoppingToken);
                    progress.Delivered = true;
                }
                if (events.Count > 0)
                {
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
