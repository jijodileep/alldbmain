using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Common;
using MySqlManager.Application.Contracts;
using MySqlManager.Application.Features.Servers;
using MySqlManager.Domain.Enums;
using MySqlManager.Domain.Migrations;
using MySqlManager.Domain.Safety;

namespace MySqlManager.Application.Features.Copies;

public sealed record StartCopyCommand(
    Guid SourceServerId,
    Guid SourceDatabaseId,
    Guid DestinationServerId,
    string DestinationDatabaseName,
    CopyMode Mode,
    ValidationLevel ValidationLevel,
    bool AcknowledgeVersionMismatch) : IRequest<JobCreatedDto>;

public sealed class StartCopyValidator : AbstractValidator<StartCopyCommand>
{
    public StartCopyValidator()
    {
        RuleFor(x => x.DestinationDatabaseName).Must(DatabaseNameRules.IsSafeDatabaseName).WithMessage("Destination database name may contain only letters, numbers, underscores, and hyphens.");
    }
}

public sealed class StartCopyHandler : IRequestHandler<StartCopyCommand, JobCreatedDto>
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IDestinationMySqlGateway _destination;
    private readonly IClock _clock;
    private readonly ICurrentUser _current;
    private readonly IAuditWriter _audit;
    private readonly Microsoft.Extensions.Options.IOptions<Configuration.JobOptions> _jobs;

    public StartCopyHandler(IApplicationDbContext db, ISecretProtector secrets, ISourceMySqlGateway source, IDestinationMySqlGateway destination, IClock clock, ICurrentUser current, IAuditWriter audit, Microsoft.Extensions.Options.IOptions<Configuration.JobOptions> jobs)
    {
        _db = db;
        _secrets = secrets;
        _ = source;
        _destination = destination;
        _clock = clock;
        _current = current;
        _audit = audit;
        _jobs = jobs;
    }

    public async Task<JobCreatedDto> Handle(StartCopyCommand request, CancellationToken cancellationToken)
    {
        if (DatabaseNameRules.IsSystemSchema(request.DestinationDatabaseName))
        {
            throw new AppValidationException([new ApiFieldError("destinationDatabaseName", "System schemas cannot be used as a destination.")]);
        }

        var sourceDb = await _db.Databases.Include(d => d.Server).ThenInclude(s => s.Credentials).FirstOrDefaultAsync(d => d.Id == request.SourceDatabaseId && d.ServerId == request.SourceServerId && !d.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Source database was not found.");
        var destination = await _db.Servers.Include(s => s.Credentials).FirstOrDefaultAsync(s => s.Id == request.DestinationServerId && !s.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Destination server was not found.");
        var sourceCredential = sourceDb.Server.Credentials.FirstOrDefault(c => c.Purpose == CredentialPurpose.BackupReadOnly && !c.IsDeleted)
            ?? throw new NotFoundException("Source server is missing a read-only backup credential.");
        var destinationCredential = destination.Credentials.FirstOrDefault(c => c.Purpose == CredentialPurpose.DestinationWrite && !c.IsDeleted)
            ?? throw new NotFoundException("Destination server is missing a destination credential. The read-only backup account is never used to write.");

        var active = await _db.MigrationJobs.AnyAsync(j => j.SourceServerId == request.SourceServerId && j.SourceDatabaseId == request.SourceDatabaseId && (j.Status == PipelineJobStatus.Pending || j.Status < PipelineJobStatus.Completed), cancellationToken);
        if (!ConcurrencyGuard.CanStart(active))
        {
            throw new ConflictException("COPY_ALREADY_RUNNING", "A copy for this source database is already active.");
        }

        var destinationEndpoint = await EndpointFactory.CreateAsync(destination, destinationCredential, _secrets, ConnectionPurpose.DestinationWrite, cancellationToken);
        if (await _destination.DatabaseExistsAsync(destinationEndpoint, request.DestinationDatabaseName, cancellationToken))
        {
            throw new DestinationExistsException(request.DestinationDatabaseName, $"{request.DestinationDatabaseName}_copy_{_clock.UtcNow:yyyyMMdd}");
        }

        var job = new MigrationJob
        {
            SourceServerId = request.SourceServerId,
            SourceDatabaseId = request.SourceDatabaseId,
            DestinationServerId = request.DestinationServerId,
            DestinationDatabaseName = request.DestinationDatabaseName,
            Mode = request.Mode,
            ValidationLevel = request.ValidationLevel,
            AcknowledgeVersionMismatch = true,
            Status = PipelineJobStatus.Pending,
            CreatedAtUtc = _clock.UtcNow,
            CreatedBy = _current.UserId,
            MaxAttempts = _jobs.Value.MaxAttempts,
            SourceModification = "NONE"
        };
        _db.MigrationJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken);
        await _audit.WriteAsync(new AuditWriteRequest(AuditActions.MigrationStarted, "MigrationJob", job.Id.ToString(), job.SourceServerId, job.SourceDatabaseId, job.DestinationServerId, job.DestinationDatabaseName, Domain.Enums.AuditResult.Success, "Copy queued. Source modification: NONE."), cancellationToken);
        return new JobCreatedDto(job.Id, job.Status.ToString());
    }
}

public sealed record StartCopyBatchCommand(
    Guid SourceServerId,
    IReadOnlyList<Guid> SourceDatabaseIds,
    Guid DestinationServerId,
    string? NameSuffix,
    CopyMode Mode,
    ValidationLevel ValidationLevel,
    bool AcknowledgeVersionMismatch) : IRequest<CopyBatchResultDto>;

public sealed class StartCopyBatchValidator : AbstractValidator<StartCopyBatchCommand>
{
    public StartCopyBatchValidator()
    {
        RuleFor(x => x.SourceDatabaseIds).NotEmpty().WithMessage("Select at least one database.");
        RuleFor(x => x.SourceDatabaseIds).Must(ids => ids is not null && ids.Count <= 200).WithMessage("Select at most 200 databases at a time.");
    }
}

public sealed class StartCopyBatchHandler : IRequestHandler<StartCopyBatchCommand, CopyBatchResultDto>
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IDestinationMySqlGateway _destination;
    private readonly IClock _clock;
    private readonly ICurrentUser _current;
    private readonly IAuditWriter _audit;
    private readonly Microsoft.Extensions.Options.IOptions<Configuration.JobOptions> _jobs;

    public StartCopyBatchHandler(IApplicationDbContext db, ISecretProtector secrets, ISourceMySqlGateway source, IDestinationMySqlGateway destination, IClock clock, ICurrentUser current, IAuditWriter audit, Microsoft.Extensions.Options.IOptions<Configuration.JobOptions> jobs)
    {
        _db = db;
        _secrets = secrets;
        _ = source;
        _destination = destination;
        _clock = clock;
        _current = current;
        _audit = audit;
        _jobs = jobs;
    }

    public async Task<CopyBatchResultDto> Handle(StartCopyBatchCommand request, CancellationToken cancellationToken)
    {
        var ids = request.SourceDatabaseIds.Distinct().ToList();
        var sameServer = request.SourceServerId == request.DestinationServerId;
        if (sameServer && string.IsNullOrWhiteSpace(request.NameSuffix))
        {
            throw new AppValidationException([new ApiFieldError("nameSuffix", "A name suffix is required when cloning on the same server so the new database does not replace the source.")]);
        }

        var destination = await _db.Servers.Include(s => s.Credentials).FirstOrDefaultAsync(s => s.Id == request.DestinationServerId && !s.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Destination server was not found.");
        var source = await _db.Servers.Include(s => s.Credentials).FirstOrDefaultAsync(s => s.Id == request.SourceServerId && !s.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Source server was not found.");
        var sourceCredential = source.Credentials.FirstOrDefault(c => c.Purpose == CredentialPurpose.BackupReadOnly && !c.IsDeleted)
            ?? throw new NotFoundException("Source server is missing a read-only backup credential.");
        _ = destination.Credentials.FirstOrDefault(c => c.Purpose == CredentialPurpose.DestinationWrite && !c.IsDeleted)
            ?? throw new NotFoundException("Destination server is missing a destination credential. The read-only backup account is never used to write.");

        var destinationCredential = destination.Credentials.First(c => c.Purpose == CredentialPurpose.DestinationWrite && !c.IsDeleted);
        var destinationEndpoint = await EndpointFactory.CreateAsync(destination, destinationCredential, _secrets, ConnectionPurpose.DestinationWrite, cancellationToken);

        var databases = await _db.Databases.Where(d => d.ServerId == request.SourceServerId && ids.Contains(d.Id) && !d.IsDeleted).ToListAsync(cancellationToken);
        var activeIds = await _db.MigrationJobs
            .Where(j => j.SourceServerId == request.SourceServerId && ids.Contains(j.SourceDatabaseId) && j.Status < PipelineJobStatus.Completed)
            .Select(j => j.SourceDatabaseId)
            .ToListAsync(cancellationToken);
        var skipped = new List<CopySkipDto>();
        var jobs = new List<MigrationJob>();
        var mode = sameServer ? CopyMode.Clone : request.Mode;
        foreach (var id in ids)
        {
            var sourceDb = databases.FirstOrDefault(d => d.Id == id);
            if (sourceDb is null)
            {
                skipped.Add(new CopySkipDto(id.ToString(), "Source database was not found."));
                continue;
            }

            if (DatabaseNameRules.IsSystemSchema(sourceDb.Name))
            {
                skipped.Add(new CopySkipDto(sourceDb.Name, "System schemas are not copied."));
                continue;
            }

            var destinationName = ApplySuffix(sourceDb.Name, request.NameSuffix);
            if (!DatabaseNameRules.IsSafeDatabaseName(destinationName) || DatabaseNameRules.IsSystemSchema(destinationName))
            {
                skipped.Add(new CopySkipDto(sourceDb.Name, "The destination name is not a safe database name."));
                continue;
            }

            if (activeIds.Contains(sourceDb.Id))
            {
                skipped.Add(new CopySkipDto(sourceDb.Name, "A copy for this database is already active."));
                continue;
            }

            if (await _destination.DatabaseExistsAsync(destinationEndpoint, destinationName, cancellationToken))
            {
                skipped.Add(new CopySkipDto(sourceDb.Name, $"Destination database {destinationName} already exists and was not dropped."));
                continue;
            }

            jobs.Add(new MigrationJob
            {
                SourceServerId = request.SourceServerId,
                SourceDatabaseId = sourceDb.Id,
                DestinationServerId = request.DestinationServerId,
                DestinationDatabaseName = destinationName,
                Mode = mode,
                ValidationLevel = request.ValidationLevel,
                AcknowledgeVersionMismatch = true,
                Status = PipelineJobStatus.Pending,
                CreatedAtUtc = _clock.UtcNow,
                CreatedBy = _current.UserId,
                MaxAttempts = _jobs.Value.MaxAttempts,
                SourceModification = "NONE"
            });
        }

        if (jobs.Count > 0)
        {
            _db.MigrationJobs.AddRange(jobs);
            await _db.SaveChangesAsync(cancellationToken);
            await _audit.WriteAsync(new AuditWriteRequest(AuditActions.MigrationStarted, "MigrationJob", jobs[0].Id.ToString(), request.SourceServerId, null, request.DestinationServerId, null, Domain.Enums.AuditResult.Success, $"{jobs.Count} copy jobs queued. Source modification: NONE."), cancellationToken);
        }

        return new CopyBatchResultDto(jobs.Count, jobs.FirstOrDefault()?.Id, skipped);
    }

    private static string ApplySuffix(string name, string? suffix)
    {
        var trimmed = (suffix ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return name;
        }

        if (!trimmed.StartsWith('_') && !trimmed.StartsWith('-'))
        {
            trimmed = "_" + trimmed;
        }

        return name + trimmed;
    }
}

public sealed record ListMigrationsQuery : IRequest<IReadOnlyList<MigrationJobDto>>;
public sealed class ListMigrationsHandler : IRequestHandler<ListMigrationsQuery, IReadOnlyList<MigrationJobDto>>
{
    private readonly IApplicationDbContext _db;
    public ListMigrationsHandler(IApplicationDbContext db) => _db = db;
    public async Task<IReadOnlyList<MigrationJobDto>> Handle(ListMigrationsQuery request, CancellationToken cancellationToken)
    {
        var rows = await _db.MigrationJobs.AsNoTracking().Where(j => !j.IsDeleted).OrderByDescending(j => j.CreatedAtUtc).Take(200).ToListAsync(cancellationToken);
        return await CopyMapper.MapAsync(_db, rows, cancellationToken);
    }
}

public sealed record GetMigrationQuery(Guid Id) : IRequest<MigrationJobDto>;
public sealed class GetMigrationHandler : IRequestHandler<GetMigrationQuery, MigrationJobDto>
{
    private readonly IApplicationDbContext _db;
    public GetMigrationHandler(IApplicationDbContext db) => _db = db;
    public async Task<MigrationJobDto> Handle(GetMigrationQuery request, CancellationToken cancellationToken)
    {
        var row = await _db.MigrationJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == request.Id && !j.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Copy job was not found.");
        return (await CopyMapper.MapAsync(_db, [row], cancellationToken)).Single();
    }
}

internal static class CopyMapper
{
    public static async Task<IReadOnlyList<MigrationJobDto>> MapAsync(IApplicationDbContext db, IReadOnlyList<MigrationJob> rows, CancellationToken cancellationToken)
    {
        var serverIds = rows.SelectMany(r => new[] { r.SourceServerId, r.DestinationServerId }).Distinct().ToList();
        var dbIds = rows.Select(r => r.SourceDatabaseId).Distinct().ToList();
        var servers = await db.Servers.AsNoTracking().Where(s => serverIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        var databases = await db.Databases.AsNoTracking().Where(d => dbIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken);
        return rows.Select(r => new MigrationJobDto(
            r.Id, r.SourceServerId, servers.GetValueOrDefault(r.SourceServerId), r.SourceDatabaseId, databases.GetValueOrDefault(r.SourceDatabaseId),
            r.DestinationServerId, servers.GetValueOrDefault(r.DestinationServerId), r.DestinationDatabaseName, r.Mode.ToString(), r.Status.ToString(), r.CurrentStep,
            r.CreatedAtUtc, r.StartedAtUtc, r.CompletedAtUtc, r.TotalBytes, r.TransferredBytes, r.ProgressPercentage, r.ErrorMessage, r.ValidationStatus.ToString(), r.SourceModification, r.ReportJson)).ToList();
    }
}
