using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlManager.Application.Abstractions;
using MySqlManager.Infrastructure.Persistence;
using Xunit;

namespace MySqlManager.Api.Tests;

public class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("environment", "Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Server=127.0.0.1;Port=3306;Database=mysql_manager_test;User=root;Password=test;Connection Timeout=2;");
        builder.UseSetting("Jwt:Issuer", "MySqlManager");
        builder.UseSetting("Jwt:Audience", "MySqlManager");
        builder.UseSetting("Jwt:SigningKey", "DEV_ONLY_CHANGE_THIS_SIGNING_KEY_32CHARS");
        builder.UseSetting("Backup:MasterKeyBase64", "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=");
        builder.UseSetting("Backup:EncryptionEnabled", "true");
        builder.UseSetting("Seed:AdminEmail", "admin@localhost");
        builder.UseSetting("Seed:AdminPassword", "DevOnly!ChangeMe1");
        builder.UseSetting("Seed:SeedSampleServer", "false");
        builder.ConfigureServices(services =>
        {
            var descriptors = services.Where(service =>
                service.ServiceType == typeof(DbContextOptions<AppDbContext>) ||
                service.ServiceType == typeof(DbContextOptions) ||
                service.ServiceType == typeof(AppDbContext) ||
                service.ServiceType == typeof(IApplicationDbContext) ||
                (service.ServiceType.FullName?.Contains("IDbContextOptionsConfiguration", StringComparison.Ordinal) ?? false)).ToList();
            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase("mysql-manager-api-tests"));
            services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<AppDbContext>());
        });
    }
}

public class ApiAuthorizationTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;
    public ApiAuthorizationTests(ApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        _factory.CreateClient();
        await DevelopmentDataSeeder.SeedAsync(_factory.Services, false);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task LoginAndServerResponseNeverIncludeThePassword()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "admin@localhost", password = "DevOnly!ChangeMe1" });
        login.EnsureSuccessStatusCode();
        var loginJson = await login.Content.ReadAsStringAsync();
        using var loginDocument = JsonDocument.Parse(loginJson);
        Assert.True(loginDocument.RootElement.GetProperty("success").GetBoolean());
        var token = loginDocument.RootElement.GetProperty("data").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var create = await client.PostAsJsonAsync("/api/servers", new
        {
            name = "Auth-Test",
            description = "catalog only",
            host = "127.0.0.1",
            port = 1,
            environment = "Testing",
            sslEnabled = false,
            connectionTimeoutSeconds = 1,
            isActive = true,
            timeZoneId = "UTC",
            credentials = new[]
            {
                new { purpose = "BackupReadOnly", username = "backup_reader", password = "super-secret-password", secretProvider = "LocalEncrypted", externalSecretName = (string?)null }
            }
        });
        var body = await create.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.DoesNotContain("super-secret-password", body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        Assert.False(ContainsProperty(document.RootElement, "password"));
        Assert.False(ContainsProperty(document.RootElement, "secretCiphertext"));
    }

    [Fact]
    public async Task ViewerCannotDeleteAServer()
    {
        var client = _factory.CreateClient();
        var adminToken = await TokenAsync(client, "admin@localhost", "DevOnly!ChangeMe1");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var createdUser = await client.PostAsJsonAsync("/api/users", new
        {
            email = "viewer@localhost",
            displayName = "Viewer",
            password = "ViewerOnly!Change1",
            roleName = "Viewer"
        });
        Assert.True(createdUser.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict);

        var viewer = _factory.CreateClient();
        var viewerToken = await TokenAsync(viewer, "viewer@localhost", "ViewerOnly!Change1");
        viewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", viewerToken);
        var response = await viewer.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/servers/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(new { confirmation = "DELETE" })
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OperatorCannotPurgeGitHistoryOrManageUsers()
    {
        var client = _factory.CreateClient();
        var adminToken = await TokenAsync(client, "admin@localhost", "DevOnly!ChangeMe1");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var created = await client.PostAsJsonAsync("/api/users", new
        {
            email = "operator@localhost",
            displayName = "Operator",
            password = "OperatorOnly!Change1",
            roleName = "Operator"
        });
        Assert.True(created.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict);

        var operatorClient = _factory.CreateClient();
        var token = await TokenAsync(operatorClient, "operator@localhost", "OperatorOnly!Change1");
        operatorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var purge = await operatorClient.PostAsJsonAsync($"/api/git-repositories/{Guid.NewGuid()}/purge", new { confirmation = "PURGE" });
        var users = await operatorClient.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Forbidden, purge.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
    }

    private static async Task<string> TokenAsync(HttpClient client, string email, string password)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private static bool ContainsProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name) || ContainsProperty(property.Value, name))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsProperty(item, name))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
