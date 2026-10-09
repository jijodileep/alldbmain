using FluentValidation;
using MediatR;
using MySqlManager.Application.Common;

namespace MySqlManager.Application.Behaviors;

public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!_validators.Any())
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);
        var failures = (await Task.WhenAll(_validators.Select(v => v.ValidateAsync(context, cancellationToken))))
            .SelectMany(result => result.Errors)
            .Where(error => error is not null)
            .Select(error => new ApiFieldError(error.PropertyName, error.ErrorMessage))
            .ToList();

        if (failures.Count > 0)
        {
            throw new AppValidationException(failures);
        }

        return await next();
    }
}
