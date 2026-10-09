using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Common;
using MySqlManager.Application.Contracts;
using MySqlManager.Domain.Enums;
using MySqlManager.Domain.Safety;
using MySqlManager.Domain.Servers;

namespace MySqlManager.Application.Features.Servers;

public sealed record CredentialInput(CredentialPurpose Purpose, string Username, string? Password, SecretProviderKind SecretProvider, string? ExternalSecretName);

public sealed record CreateServerCommand(
    string Name,
    string? Description,
    string Host,
    int Port,
    ServerEnvironment Environment,
    bool SslEnabled,
    string? SslCertificatePem,
    int ConnectionTimeoutSeconds,
    bool IsActive,
    string? TimeZoneId,
    IReadOnlyList<CredentialInput> Credentials) : IRequest<ServerDto>;

public sealed class CreateServerValidator : AbstractValidator<CreateServerCommand>
{
    public CreateServerValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Host).NotEmpty().MaximumLength(255).Must(BeSafeHost).WithMessage("Host is invalid.");
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
        RuleFor(x => x.ConnectionTimeoutSeconds).InclusiveBetween(1, 300);
        RuleFor(x => x.Credentials).NotEmpty();
        RuleForEach(x => x.Credentials).ChildRules(c =>
        {
            c.RuleFor(v => v.Username).NotEmpty().MaximumLength(128);
            c.RuleFor(v => v.Password).NotEmpty().When(v => v.SecretProvider == SecretProviderKind.LocalEncrypted);
            c.RuleFor(v => v.ExternalSecretName).NotEmpty().When(v => v.SecretProvider != SecretProviderKind.LocalEncrypted);
        });
    }

    private static bool BeSafeHost(string host) =>
        !host.Contains("..", StringComparison.Ordinal) && !host.Contains('/') && !host.Contains('\\') && !host.Contains(' ');
}

public sealed class CreateServerHandler : IRequestHandler<CreateServerCommand, ServerDto>
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly ISourceMySqlGateway _source;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ICurrentUser _current;

    public CreateServerHandler(IApplicationDbContext db, ISecretProtector secrets, ISourceMySqlGateway source, IAuditWriter audit, IClock clock, ICurrentUser current)
    {
        _db = db;
        _secrets = secrets;
        _source = source;
        _audit = audit;
        _clock = clock;
        _current = current;
    }

    public async Task<ServerDto> Handle(CreateServerCommand request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (await _db.Servers.AnyAsync(s => s.Name == name && !s.IsDeleted, cancellationToken))
        {
            throw new ConflictException("SERVER_EXISTS", "A server with that name already exists.");
        }

        var server = new Server
        {
            Name = name,
            Description = request.Description?.Trim() ?? string.Empty,
            Host = request.Host.Trim(),
            Port = request.Port,
            Environment = request.Environment,
            SslEnabled = request.SslEnabled,
            SslCertificatePem = request.SslCertificatePem,
            ConnectionTimeoutSeconds = request.ConnectionTimeoutSeconds,
            IsActive = request.IsActive,
            TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? "UTC" : request.TimeZoneId.Trim(),
            CreatedAtUtc = _clock.UtcNow,
            CreatedBy = _current.UserId
        };

        foreach (var input in request.Credentials)
        {
            server.Credentials.Add(await CredentialFactory.CreateAsync(server, input, _secrets, _source, _clock, cancellationToken));
        }

        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(new AuditWriteRequest(AuditActions.ServerCreated, "Server", server.Id.ToString(), server.Id, null, null, null, Domain.Enums.AuditResult.Success, $"Registered server {server.Name}."), cancellationToken);
        return ServerMapper.ToDto(server);
    }
}

public sealed record UpdateServerCommand(
    Guid Id,
    string Name,
    string? Description,
    string Host,
    int Port,
    ServerEnvironment Environment,
    bool SslEnabled,
    string? SslCertificatePem,
    int ConnectionTimeoutSeconds,
    bool IsActive,
    string? TimeZoneId) : IRequest<ServerDto>;

public sealed class UpdateServerValidator : AbstractValidator<UpdateServerCommand>
{
    public UpdateServerValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Host).NotEmpty().MaximumLength(255).Must(BeSafeHost).WithMessage("Host is invalid.");
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
        RuleFor(x => x.ConnectionTimeoutSeconds).InclusiveBetween(1, 300);
    }

    private static bool BeSafeHost(string host) =>
        !host.Contains("..", StringComparison.Ordinal) && !host.Contains('/') && !host.Contains('\\') && !host.Contains(' ');
}

public sealed class UpdateServerHandler : IRequestHandler<UpdateServerCommand, ServerDto>
{
    private readonly IApplicationDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ICurrentUser _current;

    public UpdateServerHandler(IApplicationDbContext db, IAuditWriter audit, IClock clock, ICurrentUser current)
    {
        _db = db;
        _audit = audit;
        _clock = clock;
        _current = current;
    }

    public async Task<ServerDto> Handle(UpdateServerCommand request, CancellationToken cancellationToken)
    {
        var server = await _db.Servers.Include(s => s.Credentials).FirstOrDefaultAsync(s => s.Id == request.Id && !s.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Server was not found.");
        server.Name = request.Name.Trim();
        if (await _db.Servers.AnyAsync(s => s.Id != server.Id && s.Name == server.Name && !s.IsDeleted, cancellationToken))
        {
            throw new ConflictException("SERVER_EXISTS", "A server with that name already exists.");
        }
        server.Description = request.Description?.Trim() ?? string.Empty;
        server.Host = request.Host.Trim();
        server.Port = request.Port;
        server.Environment = request.Environment;
        server.SslEnabled = request.SslEnabled;
        if (request.SslCertificatePem is not null)
        {
            server.SslCertificatePem = request.SslCertificatePem;
        }
        server.ConnectionTimeoutSeconds = request.ConnectionTimeoutSeconds;
        server.IsActive = request.IsActive;
        server.TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? server.TimeZoneId : request.TimeZoneId.Trim();
        server.UpdatedAtUtc = _clock.UtcNow;
        server.UpdatedBy = _current.UserId;
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(new AuditWriteRequest(AuditActions.ServerUpdated, "Server", server.Id.ToString(), server.Id, null, null, null, Domain.Enums.AuditResult.Success, $"Updated server {server.Name}."), cancellationToken);
        return ServerMapper.ToDto(server);
    }
}

public sealed record UpsertCredentialCommand(Guid ServerId, CredentialInput Credential) : IRequest<CredentialDto>;

public sealed class UpsertCredentialHandler : IRequestHandler<UpsertCredentialCommand, CredentialDto>
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly ISourceMySqlGateway _source;
    private readonly IClock _clock;
    private readonly ICurrentUser _current;

    public UpsertCredentialHandler(IApplicationDbContext db, ISecretProtector secrets, ISourceMySqlGateway source, IClock clock, ICurrentUser current)
    {
        _db = db;
        _secrets = secrets;
        _source = source;
        _clock = clock;
        _current = current;
    }

    public async Task<CredentialDto> Handle(UpsertCredentialCommand request, CancellationToken cancellationToken)
    {
        var server = await _db.Servers.Include(s => s.Credentials).FirstOrDefaultAsync(s => s.Id == request.ServerId && !s.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Server was not found.");
        var existing = server.Credentials.FirstOrDefault(c => c.Purpose == request.Credential.Purpose && !c.IsDeleted);
        if (existing is not null)
        {
            existing.IsDeleted = true;
            existing.UpdatedAtUtc = _clock.UtcNow;
        }

        var created = await CredentialFactory.CreateAsync(server, request.Credential, _secrets, _source, _clock, cancellationToken);
        created.CreatedBy = _current.UserId;
        _db.ServerCredentials.Add(created);
        await _db.SaveChangesAsync(cancellationToken);
        return new CredentialDto(created.Id, created.Purpose, created.Username, created.SecretProvider, created.IsPrivilegeValidated, created.LastValidatedAtUtc);
    }
}

public sealed record DeleteServerCommand(Guid Id, string Confirmation) : IRequest<bool>;
public sealed class DeleteServerValidator : AbstractValidator<DeleteServerCommand>
{
    public DeleteServerValidator() => RuleFor(x => x.Confirmation).Equal(Confirmation.Delete).WithMessage("Type DELETE to continue.");
}

public sealed class DeleteServerHandler : IRequestHandler<DeleteServerCommand, bool>
{
    private readonly IApplicationDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ICurrentUser _current;

    public DeleteServerHandler(IApplicationDbContext db, IAuditWriter audit, IClock clock, ICurrentUser current)
    {
        _db = db;
        _audit = audit;
        _clock = clock;
        _current = current;
    }

    public async Task<bool> Handle(DeleteServerCommand request, CancellationToken cancellationToken)
    {
        var server = await _db.Servers.Include(s => s.Credentials).FirstOrDefaultAsync(s => s.Id == request.Id && !s.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Server was not found.");
        server.IsDeleted = true;
        server.IsActive = false;
        server.UpdatedAtUtc = _clock.UtcNow;
        server.UpdatedBy = _current.UserId;
        foreach (var credential in server.Credentials.Where(c => !c.IsDeleted))
        {
            credential.IsDeleted = true;
            credential.UpdatedAtUtc = _clock.UtcNow;
        }

        var configs = await _db.BackupConfigurations.Include(c => c.Schedule).Where(c => c.ServerId == server.Id).ToListAsync(cancellationToken);
        foreach (var config in configs)
        {
            config.IsEnabled = false;
            config.UpdatedAtUtc = _clock.UtcNow;
            if (config.Schedule is not null)
            {
                config.Schedule.IsEnabled = false;
                config.Schedule.UpdatedAtUtc = _clock.UtcNow;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(new AuditWriteRequest(AuditActions.ServerDeleted, "Server", server.Id.ToString(), server.Id, null, null, null, Domain.Enums.AuditResult.Success, "Server registration removed. No database was dropped and stored backup files were kept."), cancellationToken);
        return true;
    }
}

public sealed record GetServersQuery : IRequest<IReadOnlyList<ServerDto>>;
public sealed class GetServersHandler : IRequestHandler<GetServersQuery, IReadOnlyList<ServerDto>>
{
    private readonly IApplicationDbContext _db;
    public GetServersHandler(IApplicationDbContext db) => _db = db;
    public async Task<IReadOnlyList<ServerDto>> Handle(GetServersQuery request, CancellationToken cancellationToken)
    {
        var servers = await _db.Servers.AsNoTracking().Include(s => s.Credentials).Where(s => !s.IsDeleted).OrderBy(s => s.Name).ToListAsync(cancellationToken);
        return servers.Select(ServerMapper.ToDto).ToList();
    }
}

public sealed record GetServerQuery(Guid Id) : IRequest<ServerDto>;
public sealed class GetServerHandler : IRequestHandler<GetServerQuery, ServerDto>
{
    private readonly IApplicationDbContext _db;
    public GetServerHandler(IApplicationDbContext db) => _db = db;
    public async Task<ServerDto> Handle(GetServerQuery request, CancellationToken cancellationToken)
    {
        var server = await _db.Servers.AsNoTracking().Include(s => s.Credentials).FirstOrDefaultAsync(s => s.Id == request.Id && !s.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Server was not found.");
        return ServerMapper.ToDto(server);
    }
}

public sealed record TestConnectionCommand(Guid ServerId, CredentialPurpose Purpose) : IRequest<ConnectionTestDto>;
public sealed class TestConnectionHandler : IRequestHandler<TestConnectionCommand, ConnectionTestDto>
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly ISourceMySqlGateway _source;
    private readonly IDestinationMySqlGateway _destination;
    private readonly IBackupStorage _storage;
    private readonly IClock _clock;

    public TestConnectionHandler(IApplicationDbContext db, ISecretProtector secrets, ISourceMySqlGateway source, IDestinationMySqlGateway destination, IBackupStorage storage, IClock clock)
    {
        _db = db;
        _secrets = secrets;
        _source = source;
        _destination = destination;
        _storage = storage;
        _clock = clock;
    }

    public async Task<ConnectionTestDto> Handle(TestConnectionCommand request, CancellationToken cancellationToken)
    {
        var server = await _db.Servers.Include(s => s.Credentials).FirstOrDefaultAsync(s => s.Id == request.ServerId && !s.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Server was not found.");
        var credential = server.Credentials.FirstOrDefault(c => c.Purpose == request.Purpose && !c.IsDeleted)
            ?? throw new NotFoundException("Credential for that purpose was not found.");
        var endpoint = await EndpointFactory.CreateAsync(server, credential, _secrets, request.Purpose == CredentialPurpose.BackupReadOnly ? ConnectionPurpose.BackupReadOnly : ConnectionPurpose.DestinationWrite, cancellationToken);
        MySqlServerFacts facts;
        try
        {
            facts = request.Purpose == CredentialPurpose.BackupReadOnly
                ? await _source.GetFactsAsync(endpoint, cancellationToken)
                : await _destination.GetFactsAsync(endpoint, cancellationToken);
        }
        catch (Exception ex)
        {
            return new ConnectionTestDto(false, null, null, 0, null, null, 0, false, SecretRedactor.Clean(ex.Message));
        }

        var grantsOk = true;
        var message = "Connected";
        if (request.Purpose == CredentialPurpose.BackupReadOnly)
        {
            var grants = await _source.GetGrantsAsync(endpoint, cancellationToken);
            var analysis = MySqlGrantAnalyzer.Analyze(grants, true);
            grantsOk = analysis.IsAcceptable;
            credential.GrantsSummary = analysis.Summary.Length > 2000 ? analysis.Summary[..2000] : analysis.Summary;
            credential.IsPrivilegeValidated = grantsOk;
            credential.LastValidatedAtUtc = _clock.UtcNow;
            message = grantsOk ? "Connected with a read-only backup account." : analysis.Summary;
            if (!grantsOk)
            {
                facts = facts with { Connected = false, Error = analysis.Summary };
            }
        }
        else
        {
            credential.IsPrivilegeValidated = true;
            credential.LastValidatedAtUtc = _clock.UtcNow;
        }

        var disk = _storage.GetDiskUsage();
        await _db.SaveChangesAsync(cancellationToken);
        return new ConnectionTestDto(facts.Connected && grantsOk, facts.MySqlVersion, facts.UptimeSeconds, facts.DatabaseCount, disk.FreeBytes, disk.TotalBytes, facts.LatencyMs, grantsOk, message);
    }
}

public sealed record GetServerHealthQuery(Guid ServerId) : IRequest<HealthDto>;
public sealed class GetServerHealthHandler : IRequestHandler<GetServerHealthQuery, HealthDto>
{
    private readonly IApplicationDbContext _db;
    public GetServerHealthHandler(IApplicationDbContext db) => _db = db;
    public async Task<HealthDto> Handle(GetServerHealthQuery request, CancellationToken cancellationToken)
    {
        var server = await _db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.ServerId && !s.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Server was not found.");
        var snap = await _db.ServerHealthSnapshots.AsNoTracking().Where(s => s.ServerId == server.Id).OrderByDescending(s => s.CapturedAtUtc).FirstOrDefaultAsync(cancellationToken);
        if (snap is null)
        {
            return new HealthDto(server.Id, server.Name, false, null, null, null, null, null, null, null, null, null, null, null, null, DateTime.UnixEpoch);
        }
        return new HealthDto(server.Id, server.Name, snap.IsOnline, snap.MySqlVersion, snap.UptimeSeconds, snap.DatabaseCount, snap.LatencyMs, snap.BackupStorageFreeBytes, snap.BackupStorageTotalBytes, snap.HostCpuPercent, snap.HostMemoryPercent, snap.HostDiskFreeBytes, snap.LastBackupAtUtc, snap.LastBackupStatus, snap.LastMigrationAtUtc, snap.CapturedAtUtc);
    }
}

internal static class CredentialFactory
{
    public static async Task<ServerCredential> CreateAsync(Server server, CredentialInput input, ISecretProtector secrets, ISourceMySqlGateway source, IClock clock, CancellationToken cancellationToken)
    {
        var protectedSecret = await secrets.ProtectAsync(input.Password ?? string.Empty, input.SecretProvider, input.ExternalSecretName, cancellationToken);
        var credential = new ServerCredential
        {
            ServerId = server.Id,
            Purpose = input.Purpose,
            Username = input.Username.Trim(),
            SecretCiphertext = protectedSecret.Ciphertext,
            SecretNonce = protectedSecret.Nonce,
            KeyId = protectedSecret.KeyId,
            SecretProvider = input.SecretProvider,
            ExternalSecretName = protectedSecret.ExternalName,
            CreatedAtUtc = clock.UtcNow
        };

        if (input.Purpose != CredentialPurpose.BackupReadOnly)
        {
            return credential;
        }

        try
        {
            var endpoint = new MySqlEndpoint(server.Host, server.Port, credential.Username, input.SecretProvider == SecretProviderKind.LocalEncrypted ? input.Password ?? string.Empty : await secrets.UnprotectAsync(protectedSecret, input.SecretProvider, cancellationToken), server.SslEnabled, server.SslCertificatePem, server.ConnectionTimeoutSeconds, ConnectionPurpose.BackupReadOnly);
            var grants = await source.GetGrantsAsync(endpoint, cancellationToken);
            var analysis = MySqlGrantAnalyzer.Analyze(grants, true);
            if (!analysis.IsAcceptable)
            {
                throw new PermanentJobException(PermanentErrorCode.PrivilegeFailure, analysis.Summary);
            }
            credential.IsPrivilegeValidated = true;
            credential.LastValidatedAtUtc = clock.UtcNow;
            credential.GrantsSummary = analysis.Summary.Length > 2000 ? analysis.Summary[..2000] : analysis.Summary;
        }
        catch (PermanentJobException)
        {
            throw;
        }
        catch
        {
            credential.IsPrivilegeValidated = false;
        }

        return credential;
    }
}

public static class EndpointFactory
{
    public static async Task<MySqlEndpoint> CreateAsync(Server server, ServerCredential credential, ISecretProtector secrets, ConnectionPurpose purpose, CancellationToken cancellationToken)
    {
        var password = await secrets.UnprotectAsync(new ProtectedSecret(credential.SecretCiphertext, credential.SecretNonce, credential.KeyId, credential.ExternalSecretName), credential.SecretProvider, cancellationToken);
        return new MySqlEndpoint(server.Host, server.Port, credential.Username, password, server.SslEnabled, server.SslCertificatePem, server.ConnectionTimeoutSeconds, purpose);
    }
}

internal static class ServerMapper
{
    public static ServerDto ToDto(Server server) => new(
        server.Id,
        server.Name,
        server.Description,
        server.Host,
        server.Port,
        server.Environment,
        server.SslEnabled,
        !string.IsNullOrWhiteSpace(server.SslCertificatePem),
        server.ConnectionTimeoutSeconds,
        server.IsActive,
        server.TimeZoneId,
        server.Credentials.Where(c => !c.IsDeleted).Select(c => new CredentialDto(c.Id, c.Purpose, c.Username, c.SecretProvider, c.IsPrivilegeValidated, c.LastValidatedAtUtc)).ToList());
}
