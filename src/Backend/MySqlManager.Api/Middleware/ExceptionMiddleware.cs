using MySqlManager.Application.Common;

namespace MySqlManager.Api.Middleware;

public sealed class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AppException ex)
        {
            _logger.LogWarning("Request failed with {MessageKey}", ex.MessageKey);
            context.Response.StatusCode = ex.StatusCode;
            await context.Response.WriteAsJsonAsync(ApiResponse<object>.Fail(ex.MessageKey, SecretRedactor.Clean(ex.Message), ex.Errors));
        }
        catch (FluentValidation.ValidationException ex)
        {
            var errors = ex.Errors.Select(error => new ApiFieldError(error.PropertyName, error.ErrorMessage)).ToList();
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(ApiResponse<object>.Fail("VALIDATION_ERROR", "Validation failed", errors));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled request failure");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(ApiResponse<object>.Fail("UNEXPECTED_ERROR", "The request could not be completed."));
        }
    }
}

public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task Invoke(HttpContext context)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Permitted-Cross-Domain-Policies"] = "none";
        context.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
        if (!context.Request.Path.StartsWithSegments("/swagger"))
        {
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        }
        return _next(context);
    }
}
