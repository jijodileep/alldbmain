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

namespace MySqlManager.Application.Features.Restores;

public sealed record StartRestoreCommand(Guid BackupFileId, Guid DestinationServerId, string DestinationDatabaseName, ValidationLevel ValidationLevel, string Confirmation) : IRequest<JobCreatedDto>;
public sealed class StartRestoreValidator : AbstractValidator<StartRestoreCommand>
{
    public StartRestoreValidator()
    {
        RuleFor(x => x.Confirmation).Equal(Confirmation.Restore).WithMessage("Type RESTORE to continue. This creates a new destination database and does not modify the source.");
        RuleFor(x => x.DestinationDatabaseName).Must(DatabaseNameRules.IsSafeDatabaseName);
    }
}

public sealed class StartRestoreHandler : IRequestHandler<StartRestoreCommand, JobCreatedDto>
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly IDestinationMySqlGateway _destination;
    private readonly IClock _clock;
    private readonly ICurrentUser _current;
    private readonly IAuditWriter _audit;
    private readonly Microsoft.Extensions.Options.IOptions<Configuration.JobOptions> _jobs;

    public StartRestoreHandler(IApplicationDbContext db, ISecretProtector secrets, IDestinationMySqlGateway destination, IClock clock, ICurrentUser current, IAuditWriter audit, Microsoft.Extensions.Options.IOptions<Configuration.JobOptions> jobs)
    {
        _db = db;
        _secrets = secrets;
        _destination = destination;
        _clock = clock;
        _current = current;
        _audit = audit;
        _jobs = jobs;
    }

    public async Task<JobCreatedDto> Handle(StartRestoreCommand request, CancellationToken cancellationToken)
    {
        var backup = await _db.BackupFiles.FirstOrDefaultAsync(f => f.Id == request.BackupFileId && !f.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Backup was not found.");
        if (!BackupSafetyRules.IsValidRestorePoint(backup.Status, backup.VerificationStatus))
        {
            throw new ConflictException("BACKUP_NOT_VERIFIED", "Only a completed and verified backup can be restored.");
        }

        var destination = await _db.Servers.Include(s => s.Credentials).FirstOrDefaultAsync(s => s.Id == request.DestinationServerId && !s.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Destination server was not found.");
        var credential = destination.Credentials.FirstOrDefault(c => c.Purpose == CredentialPurpose.DestinationWrite && !c.IsDeleted)
            ?? throw new NotFoundException("Destination server is missing a destination credential.");
        var endpoint = await EndpointFactory.CreateAsync(destination, credential, _secrets, ConnectionPurpose.DestinationWrite, cancellationToken);
        var databaseName = await NextAvailableNameAsync(endpoint, request.DestinationDatabaseName, cancellationToken);

        var job = new RestoreJob
        {
            BackupFileId = backup.Id,
            DestinationServerId = destination.Id,
            DestinationDatabaseName = databaseName,
            ValidationLevel = request.ValidationLevel,
            Status = PipelineJobStatus.Pending,
            ConfirmationAccepted = true,
            CreatedAtUtc = _clock.UtcNow,
            CreatedBy = _current.UserId,
            MaxAttempts = _jobs.Value.MaxAttempts,
            SourceModification = "NONE"
        };
        _db.RestoreJobs.Add(job);
        await _db.SaveChangesAsync(cancellationToken);
        var note = databaseName == request.DestinationDatabaseName
            ? $"Restore queued as {databaseName}. Source modification: NONE."
            : $"Restore queued as {databaseName} because {request.DestinationDatabaseName} already exists. Source modification: NONE.";
        await _audit.WriteAsync(new AuditWriteRequest(AuditActions.RestoreStarted, "RestoreJob", job.Id.ToString(), backup.ServerId, backup.DatabaseId, destination.Id, databaseName, Domain.Enums.AuditResult.Success, note), cancellationToken);
        return new JobCreatedDto(job.Id, job.Status.ToString(), note);
    }

    private async Task<string> NextAvailableNameAsync(MySqlEndpoint endpoint, string requestedName, CancellationToken cancellationToken)
    {
        if (!await _destination.DatabaseExistsAsync(endpoint, requestedName, cancellationToken))
        {
            return requestedName;
        }

        var stamp = _clock.UtcNow.ToString("yyyyMMdd");
        for (var n = 1; n <= 50; n++)
        {
            var suffix = n == 1 ? $"_restore_{stamp}" : $"_restore_{stamp}_{n}";
            var stem = requestedName.Length + suffix.Length <= 64 ? requestedName : requestedName[..(64 - suffix.Length)];
            var candidate = stem + suffix;
            if (!await _destination.DatabaseExistsAsync(endpoint, candidate, cancellationToken))
            {
                return candidate;
            }
        }

        throw new ConflictException("DESTINATION_ALREADY_EXISTS", "Could not find a free database name. Enter a different name. The existing database is left unchanged.");
    }
}

public sealed record ListRestoresQuery : IRequest<IReadOnlyList<RestoreJobDto>>;
public sealed class ListRestoresHandler : IRequestHandler<ListRestoresQuery, IReadOnlyList<RestoreJobDto>>
{
    private readonly IApplicationDbContext _db;
    public ListRestoresHandler(IApplicationDbContext db) => _db = db;
    public async Task<IReadOnlyList<RestoreJobDto>> Handle(ListRestoresQuery request, CancellationToken cancellationToken)
    {
        var rows = await _db.RestoreJobs.AsNoTracking().Where(j => !j.IsDeleted).OrderByDescending(j => j.CreatedAtUtc).Take(200).ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    internal static RestoreJobDto ToDto(RestoreJob job) => new(job.Id, job.BackupFileId, job.DestinationServerId, job.DestinationDatabaseName, job.Status.ToString(), job.CurrentStep, job.CreatedAtUtc, job.StartedAtUtc, job.CompletedAtUtc, job.TotalBytes, job.TransferredBytes, job.ProgressPercentage, job.ErrorMessage, job.ValidationStatus.ToString(), job.SourceModification, job.ReportJson);
}

public sealed record GetRestoreQuery(Guid Id) : IRequest<RestoreJobDto>;
public sealed class GetRestoreHandler : IRequestHandler<GetRestoreQuery, RestoreJobDto>
{
    private readonly IApplicationDbContext _db;
    public GetRestoreHandler(IApplicationDbContext db) => _db = db;
    public async Task<RestoreJobDto> Handle(GetRestoreQuery request, CancellationToken cancellationToken)
    {
        var row = await _db.RestoreJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == request.Id && !j.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Restore job was not found.");
        return ListRestoresHandler.ToDto(row);
    }
}
