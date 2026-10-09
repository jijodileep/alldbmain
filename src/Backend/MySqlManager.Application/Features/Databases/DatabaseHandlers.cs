using MediatR;
using Microsoft.EntityFrameworkCore;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Common;
using MySqlManager.Application.Contracts;
using MySqlManager.Application.Features.Servers;
using MySqlManager.Domain.Enums;
using MySqlManager.Domain.Safety;
using MySqlManager.Domain.Servers;

namespace MySqlManager.Application.Features.Databases;

public sealed record DiscoverDatabasesCommand(Guid ServerId) : IRequest<IReadOnlyList<DatabaseDto>>;
public sealed class DiscoverDatabasesHandler : IRequestHandler<DiscoverDatabasesCommand, IReadOnlyList<DatabaseDto>>
{
    private readonly IApplicationDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly ISourceMySqlGateway _source;
    private readonly IClock _clock;

    public DiscoverDatabasesHandler(IApplicationDbContext db, ISecretProtector secrets, ISourceMySqlGateway source, IClock clock)
    {
        _db = db;
        _secrets = secrets;
        _source = source;
        _clock = clock;
    }

    public async Task<IReadOnlyList<DatabaseDto>> Handle(DiscoverDatabasesCommand request, CancellationToken cancellationToken)
    {
        var server = await _db.Servers.Include(s => s.Credentials).Include(s => s.Databases)
            .FirstOrDefaultAsync(s => s.Id == request.ServerId && !s.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Server was not found.");
        var credential = server.Credentials.FirstOrDefault(c => c.Purpose == CredentialPurpose.BackupReadOnly && !c.IsDeleted)
            ?? throw new NotFoundException("A read-only backup credential is required.");
        var endpoint = await EndpointFactory.CreateAsync(server, credential, _secrets, ConnectionPurpose.BackupReadOnly, cancellationToken);
        var discovered = await _source.DiscoverAsync(endpoint, cancellationToken);
        var visible = discovered.Where(d => !DatabaseNameRules.IsSystemSchema(d.Name) && DatabaseNameRules.IsSafeDatabaseName(d.Name)).ToList();
        if (visible.Count == 0)
        {
            throw new ConflictException("NO_DATABASES_VISIBLE", "This backup account cannot see any user databases. Grant SELECT, SHOW VIEW, TRIGGER, EVENT, and LOCK TABLES on each database you want to back up, then discover again. The name your_database in the sample SQL is only a placeholder.");
        }

        foreach (var item in visible)
        {
            var existing = server.Databases.FirstOrDefault(d => d.Name == item.Name && !d.IsDeleted);
            if (existing is null)
            {
                existing = new ManagedDatabase { ServerId = server.Id, Name = item.Name, CreatedAtUtc = _clock.UtcNow };
                _db.Databases.Add(existing);
            }
            existing.SizeBytes = item.SizeBytes;
            existing.TableCount = item.TableCount;
            existing.EstimatedRows = item.EstimatedRows;
            existing.Charset = item.Charset;
            existing.Collation = item.Collation;
            existing.LastDiscoveredAtUtc = _clock.UtcNow;
            existing.UpdatedAtUtc = _clock.UtcNow;
        }
        await _db.SaveChangesAsync(cancellationToken);
        return await ListAsync(_db, server.Id, cancellationToken);
    }

    internal static async Task<IReadOnlyList<DatabaseDto>> ListAsync(IApplicationDbContext db, Guid? serverId, CancellationToken cancellationToken)
    {
        var query = db.Databases.AsNoTracking().Include(d => d.Server).Where(d => !d.IsDeleted && !d.Server.IsDeleted);
        if (serverId is not null)
        {
            query = query.Where(d => d.ServerId == serverId);
        }
        var rows = await query.OrderBy(d => d.Server.Name).ThenBy(d => d.Name).ToListAsync(cancellationToken);
        return rows.Select(d => new DatabaseDto(d.Id, d.ServerId, d.Server.Name, d.Name, d.SizeBytes, d.TableCount, d.EstimatedRows, d.Charset, d.Collation, d.IsSelectedForBackup, d.IsActive, d.LastDiscoveredAtUtc)).ToList();
    }
}

public sealed record ListDatabasesQuery(Guid? ServerId) : IRequest<IReadOnlyList<DatabaseDto>>;
public sealed class ListDatabasesHandler : IRequestHandler<ListDatabasesQuery, IReadOnlyList<DatabaseDto>>
{
    private readonly IApplicationDbContext _db;
    public ListDatabasesHandler(IApplicationDbContext db) => _db = db;
    public Task<IReadOnlyList<DatabaseDto>> Handle(ListDatabasesQuery request, CancellationToken cancellationToken) =>
        DiscoverDatabasesHandler.ListAsync(_db, request.ServerId, cancellationToken);
}

public sealed record GetDatabaseQuery(Guid Id) : IRequest<DatabaseDto>;
public sealed class GetDatabaseHandler : IRequestHandler<GetDatabaseQuery, DatabaseDto>
{
    private readonly IApplicationDbContext _db;
    public GetDatabaseHandler(IApplicationDbContext db) => _db = db;
    public async Task<DatabaseDto> Handle(GetDatabaseQuery request, CancellationToken cancellationToken)
    {
        var row = await _db.Databases.AsNoTracking().Include(d => d.Server).FirstOrDefaultAsync(d => d.Id == request.Id && !d.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Database was not found.");
        return new DatabaseDto(row.Id, row.ServerId, row.Server.Name, row.Name, row.SizeBytes, row.TableCount, row.EstimatedRows, row.Charset, row.Collation, row.IsSelectedForBackup, row.IsActive, row.LastDiscoveredAtUtc);
    }
}

public sealed record SelectDatabasesCommand(Guid ServerId, IReadOnlyList<Guid> DatabaseIds) : IRequest<IReadOnlyList<DatabaseDto>>;
public sealed class SelectDatabasesHandler : IRequestHandler<SelectDatabasesCommand, IReadOnlyList<DatabaseDto>>
{
    private readonly IApplicationDbContext _db;
    private readonly IClock _clock;
    public SelectDatabasesHandler(IApplicationDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }
    public async Task<IReadOnlyList<DatabaseDto>> Handle(SelectDatabasesCommand request, CancellationToken cancellationToken)
    {
        var rows = await _db.Databases.Where(d => d.ServerId == request.ServerId && !d.IsDeleted).ToListAsync(cancellationToken);
        var selected = request.DatabaseIds.ToHashSet();
        foreach (var row in rows)
        {
            row.IsSelectedForBackup = selected.Contains(row.Id);
            row.UpdatedAtUtc = _clock.UtcNow;
        }
        await _db.SaveChangesAsync(cancellationToken);
        return await DiscoverDatabasesHandler.ListAsync(_db, request.ServerId, cancellationToken);
    }
}

public sealed record DeactivateDatabaseCommand(Guid Id) : IRequest<bool>;
public sealed class DeactivateDatabaseHandler : IRequestHandler<DeactivateDatabaseCommand, bool>
{
    private readonly IApplicationDbContext _db;
    private readonly IClock _clock;
    public DeactivateDatabaseHandler(IApplicationDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }
    public async Task<bool> Handle(DeactivateDatabaseCommand request, CancellationToken cancellationToken)
    {
        var row = await _db.Databases.FirstOrDefaultAsync(d => d.Id == request.Id && !d.IsDeleted, cancellationToken)
            ?? throw new NotFoundException("Database was not found.");
        row.IsActive = false;
        row.IsSelectedForBackup = false;
        row.UpdatedAtUtc = _clock.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
