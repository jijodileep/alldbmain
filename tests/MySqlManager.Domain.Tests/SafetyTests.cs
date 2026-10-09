using MySqlManager.Domain.Enums;
using MySqlManager.Domain.Safety;

namespace MySqlManager.Domain.Tests;

public class RetentionPlannerTests
{
    private sealed record Point(string Name, DateTime CreatedAtUtc);

    [Fact]
    public void FailedEighthDayDoesNotDeleteTheFirstBackup()
    {
        var successes = Enumerable.Range(1, 7)
            .Select(day => new Point($"day-{day}", new DateTime(2026, 10, day, 2, 0, 0, DateTimeKind.Utc)))
            .ToList();
        var failedEighth = new Point("day-8-failed", new DateTime(2026, 10, 8, 2, 0, 0, DateTimeKind.Utc));

        Assert.False(BackupSafetyRules.MayDeleteOlderBackups(BackupJobStatus.Failed, VerificationStatus.Failed));
        Assert.DoesNotContain(failedEighth, successes);

        var expired = RetentionPlanner.SelectExpired(successes, point => point.CreatedAtUtc, 7);

        Assert.Empty(expired);
    }

    [Fact]
    public void VerifiedEighthDayRemovesOnlyTheOldestSuccess()
    {
        var successes = Enumerable.Range(1, 8)
            .Select(day => new Point($"day-{day}", new DateTime(2026, 10, day, 2, 0, 0, DateTimeKind.Utc)))
            .ToList();

        Assert.True(BackupSafetyRules.MayDeleteOlderBackups(BackupJobStatus.Completed, VerificationStatus.Passed));
        var expired = RetentionPlanner.SelectExpired(successes, point => point.CreatedAtUtc, 7);

        Assert.Single(expired);
        Assert.Equal("day-1", expired[0].Name);
    }

    [Fact]
    public void UnverifiedBackupsAreNotRestorePoints()
    {
        Assert.False(BackupSafetyRules.IsValidRestorePoint(BackupFileStatus.Completed, VerificationStatus.Pending));
        Assert.False(BackupSafetyRules.IsValidRestorePoint(BackupFileStatus.Failed, VerificationStatus.Failed));
        Assert.True(BackupSafetyRules.IsValidRestorePoint(BackupFileStatus.Completed, VerificationStatus.Passed));
    }

    [Fact]
    public void ChecksumMismatchBlocksRestore()
    {
        Assert.False(BackupSafetyRules.IsValidRestorePoint(BackupFileStatus.Completed, VerificationStatus.Failed));
    }

    [Fact]
    public void WorkingTreeKeepsTheLatestSevenVerifiedFiles()
    {
        var files = Enumerable.Range(1, 10)
            .Select(day => new Point($"day-{day}.sql.gz.enc", new DateTime(2026, 10, day, 2, 0, 0, DateTimeKind.Utc)))
            .ToList();
        var expired = RetentionPlanner.SelectExpired(files, point => point.CreatedAtUtc, 7);
        var kept = files.Except(expired).Select(point => point.Name).ToArray();
        Assert.Equal(7, kept.Length);
        Assert.Contains("day-10.sql.gz.enc", kept);
        Assert.Contains("day-4.sql.gz.enc", kept);
        Assert.DoesNotContain("day-1.sql.gz.enc", kept);
        Assert.DoesNotContain("day-2.sql.gz.enc", kept);
        Assert.DoesNotContain("day-3.sql.gz.enc", kept);
    }
}

public class SourceSafetyTests
{
    [Fact]
    public void CatalogQueriesAreReadOnly()
    {
        foreach (var sql in SourceSqlCatalog.All)
        {
            SourceSqlGuard.EnsureReadOnly(sql);
        }
    }

    [Theory]
    [InlineData("DROP DATABASE ozone_erp")]
    [InlineData("DELETE FROM accounts")]
    [InlineData("UPDATE accounts SET name = 'x'")]
    [InlineData("INSERT INTO accounts VALUES (1)")]
    [InlineData("TRUNCATE TABLE accounts")]
    [InlineData("ALTER TABLE accounts ADD COLUMN x int")]
    [InlineData("SELECT * FROM accounts INTO OUTFILE '/tmp/a'")]
    public void MutatingSqlIsRejected(string sql)
    {
        Assert.Throws<SourceSafetyException>(() => SourceSqlGuard.EnsureReadOnly(sql));
    }

    [Fact]
    public void ShowCreateTableIsAllowed()
    {
        SourceSqlGuard.EnsureReadOnly("SHOW CREATE TABLE accounts");
    }

    [Fact]
    public void WriteGrantsAreRejected()
    {
        var analysis = MySqlGrantAnalyzer.Analyze(["GRANT SELECT, INSERT, UPDATE, DELETE ON *.* TO 'backup'@'%'"], true);
        Assert.False(analysis.IsAcceptable);
    }

    [Fact]
    public void BackupGrantsAreAccepted()
    {
        var analysis = MySqlGrantAnalyzer.Analyze(["GRANT SELECT, SHOW VIEW, TRIGGER, EVENT, LOCK TABLES ON *.* TO 'backup'@'%'"], true);
        Assert.True(analysis.IsAcceptable);
    }

    [Fact]
    public void PathTraversalIsRejected()
    {
        Assert.Throws<SourceSafetyException>(() => BackupPathRules.SanitizeSegment("../etc"));
        Assert.Throws<SourceSafetyException>(() => BackupPathRules.EnsureInsideRoot(@"C:\data\backups", @"C:\data\backups\..\windows"));
    }

    [Fact]
    public void GitPurgeRequiresBothFlags()
    {
        Assert.False(GitHistoryPolicy.CanPurge(false, false));
        Assert.False(GitHistoryPolicy.CanPurge(true, false));
        Assert.False(GitHistoryPolicy.CanPurge(false, true));
        Assert.True(GitHistoryPolicy.CanPurge(true, true));
    }

    [Fact]
    public void SecondJobForTheSameDatabaseIsBlocked()
    {
        Assert.False(ConcurrencyGuard.CanStart(anotherActiveJobExistsForSameTarget: true));
        Assert.True(ConcurrencyGuard.CanStart(anotherActiveJobExistsForSameTarget: false));
    }

    [Fact]
    public void PermanentErrorsAreNotRetried()
    {
        Assert.False(RetrySchedule.ShouldRetry(permanent: true, attempt: 1, maxAttempts: 3));
        Assert.True(RetrySchedule.ShouldRetry(permanent: false, attempt: 1, maxAttempts: 3));
        Assert.False(RetrySchedule.ShouldRetry(permanent: false, attempt: 3, maxAttempts: 3));
    }
}
