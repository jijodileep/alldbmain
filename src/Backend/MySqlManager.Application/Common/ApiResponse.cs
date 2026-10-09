namespace MySqlManager.Application.Common;

public sealed record ApiFieldError(string Field, string Message);

public sealed record ApiResponse<T>
{
    public bool Success { get; init; }
    public string MessageKey { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public T? Data { get; init; }
    public IReadOnlyList<ApiFieldError> Errors { get; init; } = [];

    public static ApiResponse<T> Ok(string messageKey, string message, T data) =>
        new() { Success = true, MessageKey = messageKey, Message = message, Data = data };

    public static ApiResponse<T> Fail(string messageKey, string message, IReadOnlyList<ApiFieldError>? errors = null) =>
        new() { Success = false, MessageKey = messageKey, Message = message, Data = default, Errors = errors ?? [] };
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public static class Confirmation
{
    public const string Delete = "DELETE";
    public const string Purge = "PURGE";
    public const string Restore = "RESTORE";
}
