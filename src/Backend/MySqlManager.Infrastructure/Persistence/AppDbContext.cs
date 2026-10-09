using Microsoft.EntityFrameworkCore;
using MySqlManager.Application.Abstractions;
using MySqlManager.Domain.Backups;
using MySqlManager.Domain.Common;
using MySqlManager.Domain.Git;
using MySqlManager.Domain.Identity;
using MySqlManager.Domain.Migrations;
using MySqlManager.Domain.Operations;
using MySqlManager.Domain.Servers;

namespace MySqlManager.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext, IApplicationDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<ServerCredential> ServerCredentials => Set<ServerCredential>();
    public DbSet<ManagedDatabase> Databases => Set<ManagedDatabase>();
    public DbSet<ServerHealthSnapshot> ServerHealthSnapshots => Set<ServerHealthSnapshot>();
    public DbSet<BackupConfiguration> BackupConfigurations => Set<BackupConfiguration>();
    public DbSet<BackupSchedule> BackupSchedules => Set<BackupSchedule>();
    public DbSet<BackupJob> BackupJobs => Set<BackupJob>();
    public DbSet<BackupFile> BackupFiles => Set<BackupFile>();
    public DbSet<BackupRetentionPolicy> BackupRetentionPolicies => Set<BackupRetentionPolicy>();
    public DbSet<EncryptionKeyRecord> EncryptionKeys => Set<EncryptionKeyRecord>();
    public DbSet<MigrationJob> MigrationJobs => Set<MigrationJob>();
    public DbSet<MigrationValidation> MigrationValidations => Set<MigrationValidation>();
    public DbSet<RestoreJob> RestoreJobs => Set<RestoreJob>();
    public DbSet<GitRepository> GitRepositories => Set<GitRepository>();
    public DbSet<GitBackupRecord> GitBackupRecords => Set<GitBackupRecord>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationEndpoint> NotificationEndpoints => Set<NotificationEndpoint>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<JobLog> JobLogs => Set<JobLog>();
    public DbSet<JobProgressEvent> JobProgressEvents => Set<JobProgressEvent>();
    public DbSet<DistributedLockRecord> DistributedLocks => Set<DistributedLockRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasIndex(x => x.Email);
        modelBuilder.Entity<Role>().HasIndex(x => x.Name);
        modelBuilder.Entity<Permission>().HasIndex(x => x.Code).IsUnique();
        modelBuilder.Entity<UserRole>().HasKey(x => new { x.UserId, x.RoleId });
        modelBuilder.Entity<RolePermission>().HasKey(x => new { x.RoleId, x.PermissionId });
        modelBuilder.Entity<RefreshToken>().HasIndex(x => x.TokenHash);
        modelBuilder.Entity<Server>().HasIndex(x => x.Name);
        modelBuilder.Entity<ServerCredential>().HasIndex(x => new { x.ServerId, x.Purpose });
        modelBuilder.Entity<ManagedDatabase>().HasIndex(x => new { x.ServerId, x.Name });
        modelBuilder.Entity<ManagedDatabase>().ToTable("Databases");
        modelBuilder.Entity<BackupConfiguration>().HasIndex(x => new { x.ServerId, x.DatabaseId });
        modelBuilder.Entity<BackupSchedule>().HasIndex(x => x.BackupConfigurationId).IsUnique();
        modelBuilder.Entity<BackupJob>().HasIndex(x => new { x.ServerId, x.DatabaseId, x.Status, x.CreatedAtUtc });
        modelBuilder.Entity<BackupFile>().HasIndex(x => new { x.ServerId, x.DatabaseId, x.Status, x.CreatedAtUtc });
        modelBuilder.Entity<MigrationJob>().HasIndex(x => new { x.Status, x.CreatedAtUtc });
        modelBuilder.Entity<MigrationJob>().HasIndex(x => new { x.SourceServerId, x.SourceDatabaseId });
        modelBuilder.Entity<RestoreJob>().HasIndex(x => new { x.Status, x.CreatedAtUtc });
        modelBuilder.Entity<GitRepository>().HasIndex(x => x.ServerId);
        modelBuilder.Entity<AuditLog>().HasIndex(x => new { x.Timestamp, x.ServerId });
        modelBuilder.Entity<JobLog>().HasIndex(x => new { x.JobId, x.JobType });
        modelBuilder.Entity<JobProgressEvent>().HasIndex(x => x.Delivered);
        modelBuilder.Entity<ServerHealthSnapshot>().HasIndex(x => new { x.ServerId, x.CapturedAtUtc });
        modelBuilder.Entity<DistributedLockRecord>().HasKey(x => x.LockName);
        modelBuilder.Entity<EncryptionKeyRecord>().HasIndex(x => x.KeyId).IsUnique();

        modelBuilder.Entity<User>().HasMany(x => x.UserRoles).WithOne(x => x.User).HasForeignKey(x => x.UserId);
        modelBuilder.Entity<Role>().HasMany(x => x.UserRoles).WithOne(x => x.Role).HasForeignKey(x => x.RoleId);
        modelBuilder.Entity<Role>().HasMany(x => x.RolePermissions).WithOne(x => x.Role).HasForeignKey(x => x.RoleId);
        modelBuilder.Entity<Permission>().HasMany(x => x.RolePermissions).WithOne(x => x.Permission).HasForeignKey(x => x.PermissionId);
        modelBuilder.Entity<Server>().HasMany(x => x.Credentials).WithOne(x => x.Server).HasForeignKey(x => x.ServerId);
        modelBuilder.Entity<Server>().HasMany(x => x.Databases).WithOne(x => x.Server).HasForeignKey(x => x.ServerId);
        modelBuilder.Entity<Server>().HasMany(x => x.HealthSnapshots).WithOne(x => x.Server).HasForeignKey(x => x.ServerId);
        modelBuilder.Entity<BackupConfiguration>().HasOne(x => x.Schedule).WithOne(x => x.Configuration).HasForeignKey<BackupSchedule>(x => x.BackupConfigurationId);
        modelBuilder.Entity<BackupFile>().HasOne(x => x.BackupJob).WithMany().HasForeignKey(x => x.BackupJobId);
        modelBuilder.Entity<MigrationJob>().HasMany(x => x.Validations).WithOne(x => x.MigrationJob).HasForeignKey(x => x.MigrationJobId);
        modelBuilder.Entity<GitRepository>().HasMany(x => x.Records).WithOne(x => x.Repository).HasForeignKey(x => x.GitRepositoryId);
        modelBuilder.Entity<RefreshToken>().HasOne(x => x.User).WithMany(x => x.RefreshTokens).HasForeignKey(x => x.UserId);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(AuditableEntity).IsAssignableFrom(entity.ClrType))
            {
                var method = typeof(AppDbContext).GetMethod(nameof(ApplySoftDelete), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                method.MakeGenericMethod(entity.ClrType).Invoke(null, [modelBuilder]);
            }
        }
    }

    private static void ApplySoftDelete<TEntity>(ModelBuilder modelBuilder) where TEntity : AuditableEntity =>
        modelBuilder.Entity<TEntity>().HasQueryFilter(entity => !entity.IsDeleted);
}

public sealed class AppDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("MYSQL_MANAGER_CONNECTION")
            ?? "Server=localhost;Port=3306;Database=mysql_manager;User=root;Password=dev;";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql(connection, new MySqlServerVersion(new Version(8, 0, 36)))
            .Options;
        return new AppDbContext(options);
    }
}
