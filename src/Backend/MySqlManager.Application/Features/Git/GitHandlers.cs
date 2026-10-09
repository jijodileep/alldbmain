using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Common;
using MySqlManager.Application.Contracts;
using MySqlManager.Application.Features.Servers;
using MySqlManager.Domain.Enums;
using MySqlManager.Domain.Git;
using MySqlManager.Domain.Safety;

namespace MySqlManager.Application.Features.Git;

public sealed record UpsertGitRepositoryCommand(
    Guid? Id,
    Guid? ServerId,
    string Name,
    string RemoteUrl,
    string Branch,
    GitMode Mode,
    GitAuthType AuthType,
    string? Secret,
    SecretProviderKind SecretProvider,
    string? ExternalSecretName,
    bool AllowHistoryRewrite,
    bool IsEnabled) : IRequest<GitRepositoryDto>;

public sealed class UpsertGitRepositoryValidator : AbstractValidator<UpsertGitRepositoryCommand>
{
    public UpsertGitRepositoryValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.RemoteUrl).NotEmpty().Must(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("git@", StringComparison.OrdinalIgnoreCase));
        RuleFor(x => x.Branch).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Secret).NotEmpty().When(x => x.Id is null && x.SecretProvider == SecretProviderKind.LocalEncrypted);
    }
}

public sealed class UpsertGitRepositoryHandler : IRequestHandler<UpsertGitRepositoryCommand, GitRepositoryDto>
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IClock _clock;
    private readonly ICurrentUser _current;

    public UpsertGitRepositoryHandler(IApplicationDbContext db, ISecretProtector secrets, IClock clock, ICurrentUser current)
    {
        _db = db;
        _secrets = secrets;
        _clock = clock;
        _current = current;
    }

    public async Task<GitRepositoryDto> Handle(UpsertGitRepositoryCommand request, CancellationToken cancellationToken)
    {
        var existing = await _db.GitRepositories.Where(r => !r.IsDeleted).ToListAsync(cancellationToken);
        var repo = request.Id is null
            ? existing.FirstOrDefault(r => r.IsEnabled) ?? existing.FirstOrDefault()
            : existing.FirstOrDefault(r => r.Id == request.Id);
        if (repo is null)
        {
            var serverId = request.ServerId is Guid requested && requested != Guid.Empty
                ? requested
                : await _db.Servers.Where(s => !s.IsDeleted).Select(s => s.Id).FirstOrDefaultAsync(cancellationToken);
            if (serverId == Guid.Empty)
            {
                throw new NotFoundException("Register a server before saving the Git repository.");
            }

            repo = new GitRepository { ServerId = serverId, CreatedAtUtc = _clock.UtcNow, CreatedBy = _current.UserId };
            _db.GitRepositories.Add(repo);
        }

        foreach (var other in existing.Where(item => item.Id != repo.Id))
        {
            other.IsEnabled = false;
            other.UpdatedAtUtc = _clock.UtcNow;
        }

        repo.Name = request.Name.Trim();
        repo.RemoteUrl = request.RemoteUrl.Trim();
        repo.Branch = request.Branch.Trim();
        repo.Mode = request.Mode;
        repo.AuthType = request.AuthType;
        repo.AllowHistoryRewrite = request.AllowHistoryRewrite;
        repo.IsEnabled = true;
        repo.SecretProvider = request.SecretProvider;
        repo.UpdatedAtUtc = _clock.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.Secret) || request.SecretProvider != SecretProviderKind.LocalEncrypted)
        {
            var protectedSecret = await _secrets.ProtectAsync(request.Secret ?? string.Empty, request.SecretProvider, request.ExternalSecretName, cancellationToken);
            repo.SecretCiphertext = protectedSecret.Ciphertext;
            repo.SecretNonce = protectedSecret.Nonce;
            repo.KeyId = protectedSecret.KeyId;
            repo.ExternalSecretName = protectedSecret.ExternalName;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return GitMapper.ToDto(repo);
    }
}

public sealed record ListGitRepositoriesQuery : IRequest<IReadOnlyList<GitRepositoryDto>>;
public sealed class ListGitRepositoriesHandler : IRequestHandler<ListGitRepositoriesQuery, IReadOnlyList<GitRepositoryDto>>
{
    private readonly IApplicationDbContext _db;
    public ListGitRepositoriesHandler(IApplicationDbContext db) => _db = db;
    public async Task<IReadOnlyList<GitRepositoryDto>> Handle(ListGitRepositoriesQuery request, CancellationToken cancellationToken)
    {
        var rows = await _db.GitRepositories.AsNoTracking().Where(r => !r.IsDeleted).OrderBy(r => r.Name).ToListAsync(cancellationToken);
        return rows.Select(GitMapper.ToDto).ToList();
    }
}

public sealed record TestGitConnectionCommand(Guid Id) : IRequest<GitConnectionReport>;
public sealed class TestGitConnectionHandler : IRequestHandler<TestGitConnectionCommand, GitConnectionReport>
{
    private readonly IApplicationDbContext _db;
    private readonly IGitService _git;
    private readonly ISecretProtector _secrets;
    private readonly IClock _clock;

    public TestGitConnectionHandler(IApplicationDbContext db, IGitService git, ISecretProtector secrets, IClock clock)
    {
        _db = db;
        _git = git;
        _secrets = secrets;
        _clock = clock;
    }

    public async Task<GitConnectionReport> Handle(TestGitConnectionCommand request, CancellationToken cancellationToken)
    {
        var repo = await _db.GitRepositories.Include(r => r.Records).FirstOrDefaultAsync(r => r.Id == request.Id && !r.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Git repository was not found.");
        var server = await _db.Servers.FirstAsync(s => s.Id == repo.ServerId, cancellationToken);
        var work = await GitWorkFactory.CreateAsync(repo, server.Name, "connection-test", null, null, "{}", [], _secrets, cancellationToken);
        var report = await _git.TestAsync(work, cancellationToken);
        repo.LastTestedAtUtc = _clock.UtcNow;
        repo.LastAuthenticationSucceeded = report.Authenticated;
        repo.LastCommitSha = report.LastCommitSha ?? repo.LastCommitSha;
        repo.LastError = report.Error;
        await _db.SaveChangesAsync(cancellationToken);
        return report;
    }
}

public sealed record GetGitLogQuery(Guid Id) : IRequest<IReadOnlyList<GitLogDto>>;
public sealed class GetGitLogHandler : IRequestHandler<GetGitLogQuery, IReadOnlyList<GitLogDto>>
{
    private readonly IApplicationDbContext _db;
    private readonly IGitService _git;
    private readonly ISecretProtector _secrets;

    public GetGitLogHandler(IApplicationDbContext db, IGitService git, ISecretProtector secrets)
    {
        _db = db;
        _git = git;
        _secrets = secrets;
    }

    public async Task<IReadOnlyList<GitLogDto>> Handle(GetGitLogQuery request, CancellationToken cancellationToken)
    {
        var repo = await _db.GitRepositories.FirstOrDefaultAsync(r => r.Id == request.Id && !r.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Git repository was not found.");
        var server = await _db.Servers.FirstAsync(s => s.Id == repo.ServerId, cancellationToken);
        var work = await GitWorkFactory.CreateAsync(repo, server.Name, "history", null, null, "{}", [], _secrets, cancellationToken);
        var log = await _git.GetLogAsync(work, cancellationToken);
        return log.Select(entry => new GitLogDto(entry.Sha, entry.CommittedAtUtc, entry.Subject)).ToList();
    }
}

public sealed record PurgeGitHistoryCommand(Guid Id, string Confirmation) : IRequest<bool>;
public sealed class PurgeGitHistoryValidator : AbstractValidator<PurgeGitHistoryCommand>
{
    public PurgeGitHistoryValidator() => RuleFor(x => x.Confirmation).Equal(Confirmation.Purge).WithMessage("Type PURGE to continue. Older Git backup versions will become permanently unrecoverable.");
}

public sealed class PurgeGitHistoryHandler : IRequestHandler<PurgeGitHistoryCommand, bool>
{
    private readonly IApplicationDbContext _db;
    private readonly IGitService _git;
    private readonly ISecretProtector _secrets;
    private readonly IAuditWriter _audit;
    private readonly Microsoft.Extensions.Options.IOptions<Configuration.GitOptions> _gitOptions;

    public PurgeGitHistoryHandler(IApplicationDbContext db, IGitService git, ISecretProtector secrets, IAuditWriter audit, Microsoft.Extensions.Options.IOptions<Configuration.GitOptions> gitOptions)
    {
        _db = db;
        _git = git;
        _secrets = secrets;
        _audit = audit;
        _gitOptions = gitOptions;
    }

    public async Task<bool> Handle(PurgeGitHistoryCommand request, CancellationToken cancellationToken)
    {
        var repo = await _db.GitRepositories.FirstOrDefaultAsync(r => r.Id == request.Id && !r.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Git repository was not found.");
        if (!GitHistoryPolicy.CanPurge(repo.AllowHistoryRewrite, _gitOptions.Value.AllowHistoryRewrite))
        {
            throw new ConflictException("GIT_HISTORY_REWRITE_DISABLED", "Git history rewrite is disabled. Older backup versions stay recoverable from history until an administrator explicitly enables AllowGitHistoryRewrite.");
        }

        var server = await _db.Servers.FirstAsync(s => s.Id == repo.ServerId, cancellationToken);
        var work = await GitWorkFactory.CreateAsync(repo, server.Name, "purge", null, null, "{}", [], _secrets, cancellationToken);
        await _git.PurgeHistoryAsync(work, cancellationToken);
        await _audit.WriteAsync(new AuditWriteRequest(AuditActions.GitPurge, "GitRepository", repo.Id.ToString(), repo.ServerId, null, null, null, Domain.Enums.AuditResult.Success, "Git history was rewritten. Older backup blobs are no longer recoverable from this repository."), cancellationToken);
        return true;
    }
}

public static class GitWorkFactory
{
    public static async Task<GitSyncWorkItem> CreateAsync(
        GitRepository repo,
        string serverName,
        string databaseFolder,
        string? backupFilePath,
        string? backupFileName,
        string manifestJson,
        IReadOnlyList<string> retainedFileNames,
        ISecretProtector secrets,
        CancellationToken cancellationToken)
    {
        string? secret = null;
        if (!string.IsNullOrWhiteSpace(repo.SecretCiphertext) || !string.IsNullOrWhiteSpace(repo.ExternalSecretName))
        {
            secret = await secrets.UnprotectAsync(new ProtectedSecret(repo.SecretCiphertext ?? string.Empty, repo.SecretNonce ?? string.Empty, repo.KeyId ?? string.Empty, repo.ExternalSecretName), repo.SecretProvider, cancellationToken);
        }

        return new GitSyncWorkItem(
            repo.Id,
            repo.RemoteUrl,
            repo.Branch,
            repo.Mode,
            repo.AuthType,
            secret,
            repo.AllowHistoryRewrite,
            BackupPathRules.SanitizeSegment(serverName),
            BackupPathRules.SanitizeSegment(databaseFolder),
            backupFilePath,
            backupFileName,
            manifestJson,
            retainedFileNames);
    }
}

internal static class GitMapper
{
    public static GitRepositoryDto ToDto(GitRepository repo) => new(
        repo.Id, repo.ServerId, repo.Name, repo.RemoteUrl, repo.Branch, repo.Mode.ToString(), repo.AuthType.ToString(), repo.AllowHistoryRewrite, repo.IsEnabled, repo.LastPushAtUtc, repo.LastCommitSha, repo.LastError, repo.LastAuthenticationSucceeded);
}

public static class ManifestBuilder
{
    public static string Build(string server, string database, DateTime backupDateUtc, string? mysqlVersion, long fileSize, long compressedSize, string sha256, string keyId, IReadOnlyList<string> files)
    {
        var payload = new
        {
            server,
            database,
            backupType = "Full",
            backupDate = backupDateUtc.ToString("O"),
            mysqlVersion,
            fileSize,
            compressedSize,
            sha256,
            encryption = "AES-256-GCM",
            keyId,
            files
        };
        return JsonSerializer.Serialize(payload);
    }
}
