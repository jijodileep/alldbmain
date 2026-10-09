namespace MySqlManager.Domain.Safety;

public static class ConcurrencyGuard
{
    public static bool CanStart(bool anotherActiveJobExistsForSameTarget) => !anotherActiveJobExistsForSameTarget;
}

public static class RetrySchedule
{
    public static readonly TimeSpan[] DefaultDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    public static bool ShouldRetry(bool permanent, int attempt, int maxAttempts) =>
        !permanent && attempt < maxAttempts;

    public static TimeSpan DelayForAttempt(int attempt)
    {
        var index = Math.Clamp(attempt - 1, 0, DefaultDelays.Length - 1);
        return DefaultDelays[index];
    }
}
