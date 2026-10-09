using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Common;
using MySqlManager.Application.Configuration;
using MySqlManager.Domain.Enums;
using MySqlManager.Domain.Identity;
using MySqlManager.Domain.Servers;

namespace MySqlManager.Infrastructure.Persistence;

public static class DevelopmentDataSeeder
{
    public static async Task SeedAsync(IServiceProvider services, bool isDevelopment, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Database.IsRelational())
        {
            await db.Database.MigrateAsync(cancellationToken);
        }
        else
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }

        var permissions = await db.Permissions.ToListAsync(cancellationToken);
        foreach (var permission in Permissions.All)
        {
            if (permissions.All(p => p.Code != permission.Code))
            {
                db.Permissions.Add(new Permission { Code = permission.Code, Description = permission.Description });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        permissions = await db.Permissions.ToListAsync(cancellationToken);
        foreach (var roleName in new[] { "SuperAdmin", "Administrator", "Operator", "Viewer" })
        {
            var role = await db.Roles.Include(r => r.RolePermissions).FirstOrDefaultAsync(r => r.Name == roleName, cancellationToken);
            if (role is null)
            {
                role = new Role { Name = roleName, Description = roleName, CreatedAtUtc = DateTime.UtcNow };
                db.Roles.Add(role);
                await db.SaveChangesAsync(cancellationToken);
            }

            var codes = Permissions.ForRole(roleName).ToHashSet();
            var existing = role.RolePermissions.Select(rp => rp.PermissionId).ToHashSet();
            foreach (var permission in permissions.Where(p => codes.Contains(p.Code) && !existing.Contains(p.Id)))
            {
                db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var seed = scope.ServiceProvider.GetRequiredService<IOptions<SeedOptions>>().Value;
        if (!string.IsNullOrWhiteSpace(seed.AdminPassword) && !await db.Users.AnyAsync(cancellationToken))
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var adminRole = await db.Roles.FirstAsync(r => r.Name == "SuperAdmin", cancellationToken);
            var user = new User
            {
                Email = seed.AdminEmail.Trim().ToLowerInvariant(),
                UserName = seed.AdminEmail.Trim().ToLowerInvariant(),
                DisplayName = "Super Admin",
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            };
            user.PasswordHash = hasher.Hash(user, seed.AdminPassword);
            user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = adminRole.Id });
            db.Users.Add(user);
        }

        if (isDevelopment && seed.SeedSampleServer && !await db.Servers.AnyAsync(cancellationToken))
        {
            db.Servers.Add(new Server
            {
                Name = "Sample-Local",
                Description = "Development sample. This is not a production server.",
                Host = "127.0.0.1",
                Port = 3306,
                Environment = ServerEnvironment.Development,
                IsActive = false,
                TimeZoneId = "UTC",
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        if (!await db.BackupRetentionPolicies.AnyAsync(cancellationToken))
        {
            db.BackupRetentionPolicies.Add(new Domain.Backups.BackupRetentionPolicy
            {
                DailyRetentionCount = 7,
                MinimumFreeDiskPercentage = 15,
                IsDefault = true,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
