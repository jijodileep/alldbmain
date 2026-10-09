using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Common;
using MySqlManager.Application.Contracts;
using MySqlManager.Domain.Enums;
using MySqlManager.Domain.Identity;

namespace MySqlManager.Application.Features.Auth;

public sealed record LoginCommand(string Email, string Password, string IpAddress) : IRequest<AuthResultDto>;
public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public interface IAuthTokenIssuer
{
    Task<AuthResultDto> IssueAsync(User user, string ipAddress, CancellationToken cancellationToken);
}

public sealed class AuthTokenIssuer : IAuthTokenIssuer
{
    private readonly IApplicationDbContext _db;
    private readonly ITokenService _tokens;
    private readonly IClock _clock;
    private readonly Microsoft.Extensions.Options.IOptions<Configuration.JwtOptions> _jwt;

    public AuthTokenIssuer(IApplicationDbContext db, ITokenService tokens, IClock clock, Microsoft.Extensions.Options.IOptions<Configuration.JwtOptions> jwt)
    {
        _db = db;
        _tokens = tokens;
        _clock = clock;
        _jwt = jwt;
    }

    public async Task<AuthResultDto> IssueAsync(User user, string ipAddress, CancellationToken cancellationToken)
    {
        var roles = user.UserRoles.Select(ur => ur.Role.Name).Distinct().ToList();
        var permissions = user.UserRoles.SelectMany(ur => ur.Role.RolePermissions).Select(rp => rp.Permission.Code).Distinct().ToList();
        var (access, expires) = _tokens.CreateAccessToken(user, roles, permissions);
        var refresh = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(refresh))),
            ExpiresAtUtc = _clock.UtcNow.AddDays(_jwt.Value.RefreshTokenDays),
            CreatedAtUtc = _clock.UtcNow,
            CreatedByIp = ipAddress
        });
        await _db.SaveChangesAsync(cancellationToken);
        return new AuthResultDto(access, refresh, expires, new UserProfileDto(user.Id, user.Email, user.DisplayName, roles, permissions));
    }
}

public sealed class LoginHandler : IRequestHandler<LoginCommand, AuthResultDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _passwords;
    private readonly IClock _clock;
    private readonly IAuthTokenIssuer _issuer;

    public LoginHandler(IApplicationDbContext db, IPasswordHasher passwords, IClock clock, IAuthTokenIssuer issuer)
    {
        _db = db;
        _passwords = passwords;
        _clock = clock;
        _issuer = issuer;
    }

    public async Task<AuthResultDto> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Email == email && !u.IsDeleted, cancellationToken);
        if (user is null || !user.IsActive || !_passwords.Verify(user, user.PasswordHash, request.Password))
        {
            throw new ForbiddenAppException("Invalid email or password.");
        }

        user.LastLoginAtUtc = _clock.UtcNow;
        return await _issuer.IssueAsync(user, request.IpAddress, cancellationToken);
    }
}

public sealed record RefreshTokenCommand(string RefreshToken, string IpAddress) : IRequest<AuthResultDto>;
public sealed class RefreshTokenValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenValidator() => RuleFor(x => x.RefreshToken).NotEmpty();
}

public sealed class RefreshTokenHandler : IRequestHandler<RefreshTokenCommand, AuthResultDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IClock _clock;
    private readonly IAuthTokenIssuer _issuer;

    public RefreshTokenHandler(IApplicationDbContext db, IClock clock, IAuthTokenIssuer issuer)
    {
        _db = db;
        _clock = clock;
        _issuer = issuer;
    }

    public async Task<AuthResultDto> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.RefreshToken)));
        var token = await _db.RefreshTokens.Include(t => t.User).ThenInclude(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token is null || !token.IsActive(_clock.UtcNow) || !token.User.IsActive || token.User.IsDeleted)
        {
            throw new ForbiddenAppException("Refresh token is invalid.");
        }

        token.RevokedAtUtc = _clock.UtcNow;
        var issued = await _issuer.IssueAsync(token.User, request.IpAddress, cancellationToken);
        token.ReplacedByTokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(issued.RefreshToken)));
        await _db.SaveChangesAsync(cancellationToken);
        return issued;
    }
}

public sealed record LogoutCommand(string RefreshToken) : IRequest<bool>;
public sealed class LogoutHandler : IRequestHandler<LogoutCommand, bool>
{
    private readonly IApplicationDbContext _db;
    private readonly IClock _clock;

    public LogoutHandler(IApplicationDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<bool> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return true;
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.RefreshToken)));
        var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token is not null && token.RevokedAtUtc is null)
        {
            token.RevokedAtUtc = _clock.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}

public sealed record CurrentUserQuery : IRequest<UserProfileDto>;
public sealed class CurrentUserHandler : IRequestHandler<CurrentUserQuery, UserProfileDto>
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _current;

    public CurrentUserHandler(IApplicationDbContext db, ICurrentUser current)
    {
        _db = db;
        _current = current;
    }

    public async Task<UserProfileDto> Handle(CurrentUserQuery request, CancellationToken cancellationToken)
    {
        if (_current.UserId is null)
        {
            throw new ForbiddenAppException("Authentication is required.");
        }

        var user = await _db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Id == _current.UserId && !u.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("User was not found.");
        var roles = user.UserRoles.Select(ur => ur.Role.Name).Distinct().ToList();
        var permissions = user.UserRoles.SelectMany(ur => ur.Role.RolePermissions).Select(rp => rp.Permission.Code).Distinct().ToList();
        return new UserProfileDto(user.Id, user.Email, user.DisplayName, roles, permissions);
    }
}
