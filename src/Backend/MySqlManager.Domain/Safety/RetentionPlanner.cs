namespace MySqlManager.Domain.Safety;

public static class RetentionPlanner
{
    public static IReadOnlyList<T> SelectExpired<T>(
        IEnumerable<T> verifiedSuccessfulDailyBackups,
        Func<T, DateTime> createdAtUtc,
        int keepCount)
    {
        if (keepCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(keepCount), "Retention must keep at least one verified backup.");
        }

        return verifiedSuccessfulDailyBackups
            .OrderByDescending(createdAtUtc)
            .Skip(keepCount)
            .ToList();
    }
}
