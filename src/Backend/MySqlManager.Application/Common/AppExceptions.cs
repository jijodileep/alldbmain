namespace MySqlManager.Application.Common;

public abstract class AppException : Exception
{
    protected AppException(string messageKey, string message, int statusCode, IReadOnlyList<ApiFieldError>? errors = null)
        : base(message)
    {
        MessageKey = messageKey;
        StatusCode = statusCode;
        Errors = errors ?? [];
    }

    public string MessageKey { get; }
    public int StatusCode { get; }
    public IReadOnlyList<ApiFieldError> Errors { get; }
}

public sealed class RemoteConnectionException : AppException
{
    public RemoteConnectionException(string message) : base("MYSQL_CONNECTION_FAILED", message, 503)
    {
    }
}

public sealed class NotFoundException : AppException
{
    public NotFoundException(string message) : base("NOT_FOUND", message, 404)
    {
    }
}

public sealed class ForbiddenAppException : AppException
{
    public ForbiddenAppException(string message) : base("FORBIDDEN", message, 403)
    {
    }
}

public sealed class ConflictException : AppException
{
    public ConflictException(string messageKey, string message) : base(messageKey, message, 409)
    {
    }
}

public sealed class DestinationExistsException : AppException
{
    public DestinationExistsException(string databaseName, string suggestedName)
        : base(
            "DESTINATION_ALREADY_EXISTS",
            $"Destination database already exists: {databaseName}. Choose a new name such as {suggestedName}.",
            409,
            [new ApiFieldError("destinationDatabaseName", $"Database {databaseName} already exists. Suggested name: {suggestedName}.")])
    {
        SuggestedName = suggestedName;
    }

    public string SuggestedName { get; }
}

public sealed class PermanentJobException : AppException
{
    public PermanentJobException(Domain.Enums.PermanentErrorCode code, string message)
        : base(code.ToString().ToUpperInvariant(), message, 422)
    {
        Code = code;
    }

    public Domain.Enums.PermanentErrorCode Code { get; }
}

public sealed class AppValidationException : AppException
{
    public AppValidationException(IReadOnlyList<ApiFieldError> errors)
        : base("VALIDATION_ERROR", "Validation failed", 400, errors)
    {
    }
}
