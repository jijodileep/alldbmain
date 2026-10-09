using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Options;
using MySqlConnector;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Common;
using MySqlManager.Application.Configuration;
using MySqlManager.Domain.Enums;
using MySqlManager.Domain.Safety;

namespace MySqlManager.Infrastructure.MySql;

public sealed class SourceMySqlGateway : ISourceMySqlGateway
{
    public Task<MySqlServerFacts> GetFactsAsync(MySqlEndpoint endpoint, CancellationToken cancellationToken) =>
        WithConnection(endpoint, async (connection, latency, token) =>
        {
            var version = await ScalarAsync(connection, SourceSqlCatalog.Version, null, token);
            long? uptimeSeconds = null;
            SourceSqlGuard.EnsureReadOnly(SourceSqlCatalog.Uptime);
            await using (var uptimeCommand = new MySqlCommand(SourceSqlCatalog.Uptime, connection))
            await using (var uptimeReader = await uptimeCommand.ExecuteReaderAsync(token))
            {
                if (await uptimeReader.ReadAsync(token) && uptimeReader.FieldCount > 1 && long.TryParse(uptimeReader.GetValue(1)?.ToString(), out var parsed))
                {
                    uptimeSeconds = parsed;
                }
            }

            var countText = await ScalarAsync(connection, "SELECT COUNT(*) FROM information_schema.SCHEMATA", null, token);
            _ = int.TryParse(countText, out var count);
            return new MySqlServerFacts(true, version, uptimeSeconds, count, latency, null, null, null);
        }, cancellationToken);

    public Task<IReadOnlyList<string>> GetGrantsAsync(MySqlEndpoint endpoint, CancellationToken cancellationToken) =>
        WithConnection(endpoint, async (connection, _, token) =>
        {
            var rows = new List<string>();
            await using var command = new MySqlCommand(SourceSqlCatalog.Grants, connection);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                rows.Add(reader.GetString(0));
            }

            return (IReadOnlyList<string>)rows;
        }, cancellationToken);

    public Task<IReadOnlyList<DiscoveredSchema>> DiscoverAsync(MySqlEndpoint endpoint, CancellationToken cancellationToken) =>
        WithConnection(endpoint, async (connection, _, token) =>
        {
            var schemas = new Dictionary<string, DiscoveredSchema>(StringComparer.OrdinalIgnoreCase);
            await using (var command = new MySqlCommand(SourceSqlCatalog.Schemas, connection) { CommandTimeout = 60 })
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    var name = reader.GetString(0);
                    if (DatabaseNameRules.IsSystemSchema(name))
                    {
                        continue;
                    }

                    schemas[name] = new DiscoveredSchema(name, 0, 0, 0, reader.IsDBNull(1) ? string.Empty : reader.GetString(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
                }
            }

            try
            {
                await using var command = new MySqlCommand(SourceSqlCatalog.SchemaStats, connection) { CommandTimeout = 20 };
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var name = reader.GetString(0);
                    if (!schemas.TryGetValue(name, out var existing))
                    {
                        continue;
                    }

                    schemas[name] = existing with
                    {
                        TableCount = ReadInt(reader, 1),
                        SizeBytes = ReadLong(reader, 2),
                        EstimatedRows = ReadLong(reader, 3)
                    };
                }
            }
            catch (MySqlException)
            {
                // Schema names are enough to catalog databases. Size totals can be filled on a later discovery.
            }

            return (IReadOnlyList<DiscoveredSchema>)schemas.Values.OrderBy(s => s.Name).ToList();
        }, cancellationToken);

    public Task<SchemaInventory> GetInventoryAsync(MySqlEndpoint endpoint, string databaseName, bool includeChecksumSample, CancellationToken cancellationToken)
    {
        DatabaseNameRules.RequireSafe(databaseName);
        return WithConnection(endpoint, (connection, _, token) => ReadInventoryAsync(connection, databaseName, includeChecksumSample, token), cancellationToken);
    }

    internal static async Task<SchemaInventory> ReadInventoryAsync(MySqlConnection connection, string databaseName, bool includeChecksumSample, CancellationToken cancellationToken)
    {
        SourceSqlGuard.EnsureReadOnly(SourceSqlCatalog.SchemaExists);
        await using var exists = new MySqlCommand(SourceSqlCatalog.SchemaExists, connection);
        exists.Parameters.AddWithValue("@db", databaseName);
        var name = await exists.ExecuteScalarAsync(cancellationToken);
        if (name is null or DBNull)
        {
            return new SchemaInventory(false, string.Empty, string.Empty, 0, 0, 0, 0, 0, 0, 0, []);
        }

        string charset = string.Empty, collation = string.Empty;
        await using (var meta = new MySqlCommand(SourceSqlCatalog.SchemaMeta, connection))
        {
            meta.Parameters.AddWithValue("@db", databaseName);
            await using var reader = await meta.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                charset = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                collation = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            }
        }

        int tables = 0, views = 0, procedures = 0, triggers = 0, events = 0;
        await using (var counts = new MySqlCommand(SourceSqlCatalog.ObjectCounts, connection))
        {
            counts.Parameters.AddWithValue("@db", databaseName);
            await using var reader = await counts.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                tables = reader.GetInt32(0);
                views = reader.GetInt32(1);
                procedures = reader.GetInt32(2);
                triggers = reader.GetInt32(3);
                events = reader.GetInt32(4);
            }
        }

        long size = 0, rows = 0;
        await using (var stats = new MySqlCommand(SourceSqlCatalog.SchemaStats + " HAVING TABLE_SCHEMA = @db", connection))
        {
            SourceSqlGuard.EnsureReadOnly(stats.CommandText);
            stats.Parameters.AddWithValue("@db", databaseName);
            await using var reader = await stats.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                size = reader.GetInt64(2);
                rows = reader.GetInt64(3);
            }
        }

        var checksums = new List<TableChecksumSample>();
        if (includeChecksumSample)
        {
            var tableNames = new List<string>();
            await using var tablesCommand = new MySqlCommand(SourceSqlCatalog.TableNames, connection);
            tablesCommand.Parameters.AddWithValue("@db", databaseName);
            await using var reader = await tablesCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken) && tableNames.Count < 25)
            {
                tableNames.Add(reader.GetString(0));
            }

            await reader.DisposeAsync();
            foreach (var table in tableNames.Where(DatabaseNameRules.IsSafeDatabaseName))
            {
                var sql = $"CHECKSUM TABLE {DatabaseNameRules.Quote(databaseName)}.{DatabaseNameRules.Quote(table)}";
                SourceSqlGuard.EnsureReadOnly(sql);
                await using var checksum = new MySqlCommand(sql, connection);
                await using var checksumReader = await checksum.ExecuteReaderAsync(cancellationToken);
                long? value = null;
                if (await checksumReader.ReadAsync(cancellationToken) && !checksumReader.IsDBNull(1))
                {
                    value = checksumReader.GetInt64(1);
                }

                checksums.Add(new TableChecksumSample(table, value));
            }
        }

        return new SchemaInventory(true, charset, collation, tables, views, procedures, triggers, events, rows, size, checksums);
    }

    private static async Task<T> WithConnection<T>(MySqlEndpoint endpoint, Func<MySqlConnection, int, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (endpoint.Purpose != ConnectionPurpose.BackupReadOnly)
        {
            throw new SourceSafetyException("A source connection must use the read-only backup purpose.");
        }

        var started = Stopwatch.GetTimestamp();
        await using var connection = await ConnectionFactory.OpenAsync(endpoint, cancellationToken);
        var latency = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return await action(connection, latency, cancellationToken);
    }

    private static int ReadInt(MySqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));

    private static long ReadLong(MySqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal));

    private static async Task<string?> ScalarAsync(MySqlConnection connection, string sql, string? database, CancellationToken cancellationToken)
    {
        SourceSqlGuard.EnsureReadOnly(sql);
        await using var command = new MySqlCommand(sql, connection);
        if (database is not null)
        {
            command.Parameters.AddWithValue("@db", database);
        }

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value);
    }
}

public sealed class DestinationMySqlGateway : IDestinationMySqlGateway
{
    public async Task<MySqlServerFacts> GetFactsAsync(MySqlEndpoint endpoint, CancellationToken cancellationToken)
    {
        EnsureDestination(endpoint);
        var started = Stopwatch.GetTimestamp();
        await using var connection = await ConnectionFactory.OpenAsync(endpoint, cancellationToken);
        var latency = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        SourceSqlGuard.EnsureReadOnly(SourceSqlCatalog.Version);
        await using var versionCommand = new MySqlCommand(SourceSqlCatalog.Version, connection);
        var version = Convert.ToString(await versionCommand.ExecuteScalarAsync(cancellationToken));
        return new MySqlServerFacts(true, version, null, 0, latency, null, null, null);
    }

    public async Task<bool> DatabaseExistsAsync(MySqlEndpoint endpoint, string databaseName, CancellationToken cancellationToken)
    {
        EnsureDestination(endpoint);
        DatabaseNameRules.RequireSafe(databaseName);
        await using var connection = await ConnectionFactory.OpenAsync(endpoint, cancellationToken);
        SourceSqlGuard.EnsureReadOnly(SourceSqlCatalog.SchemaExists);
        await using var command = new MySqlCommand(SourceSqlCatalog.SchemaExists, connection);
        command.Parameters.AddWithValue("@db", databaseName);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null and not DBNull;
    }

    public async Task CreateDatabaseAsync(MySqlEndpoint endpoint, string databaseName, string charset, string collation, CancellationToken cancellationToken)
    {
        EnsureDestination(endpoint);
        DatabaseNameRules.RequireSafe(databaseName);
        if (!DatabaseNameRules.IsSafeIdentifier(charset) || !DatabaseNameRules.IsSafeIdentifier(collation))
        {
            throw new SourceSafetyException("Charset and collation must be simple identifiers.");
        }

        if (await DatabaseExistsAsync(endpoint, databaseName, cancellationToken))
        {
            throw new InvalidOperationException($"Destination database {databaseName} already exists. It will not be dropped or overwritten.");
        }

        await using var connection = await ConnectionFactory.OpenAsync(endpoint, cancellationToken);
        var sql = $"CREATE DATABASE {DatabaseNameRules.Quote(databaseName)} CHARACTER SET {charset} COLLATE {collation}";
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SchemaInventory> GetInventoryAsync(MySqlEndpoint endpoint, string databaseName, bool includeChecksumSample, CancellationToken cancellationToken)
    {
        EnsureDestination(endpoint);
        await using var connection = await ConnectionFactory.OpenAsync(endpoint, cancellationToken);
        return await SourceMySqlGateway.ReadInventoryAsync(connection, databaseName, includeChecksumSample, cancellationToken);
    }

    private static void EnsureDestination(MySqlEndpoint endpoint)
    {
        if (endpoint.Purpose != ConnectionPurpose.DestinationWrite)
        {
            throw new SourceSafetyException("Destination changes require a destination credential. The read-only source connection cannot be used.");
        }
    }
}

internal static class ConnectionFactory
{
    public static string Build(MySqlEndpoint endpoint)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = endpoint.Host,
            Port = (uint)endpoint.Port,
            UserID = endpoint.Username,
            Password = endpoint.Password,
            SslMode = endpoint.SslEnabled ? MySqlSslMode.Required : MySqlSslMode.Disabled,
            ConnectionTimeout = (uint)Math.Clamp(endpoint.TimeoutSeconds, 1, 300),
            AllowUserVariables = true,
            Database = string.Empty
        };
        return builder.ConnectionString;
    }

    public static async Task<MySqlConnection> OpenAsync(MySqlEndpoint endpoint, CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(Build(endpoint));
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch (MySqlException ex)
        {
            await connection.DisposeAsync();
            var detail = ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                ? "The connection timed out."
                : SecretRedactor.Clean(ex.Message);
            throw new RemoteConnectionException($"Could not connect to MySQL at {endpoint.Host}:{endpoint.Port}. {detail} Apache serves the website. Database discovery uses the MySQL port, usually 3306, and that port must accept connections from this machine.");
        }
    }
}

public sealed class MySqlDumpBackupService : IMySqlBackupService
{
    private readonly IOptions<BackupOptions> _options;
    public MySqlDumpBackupService(IOptions<BackupOptions> options) => _options = options;

    public async Task<DumpResult> DumpAsync(DumpRequest request, CancellationToken cancellationToken)
    {
        if (request.Endpoint.Purpose != ConnectionPurpose.BackupReadOnly)
        {
            throw new SourceSafetyException("mysqldump may only use a read-only source credential.");
        }

        DatabaseNameRules.RequireSafe(request.DatabaseName);
        var tempDirectory = Path.GetDirectoryName(request.GzipOutputPath)!;
        var defaultsFile = await ClientFiles.WriteDefaultsAsync(tempDirectory, request.Endpoint, cancellationToken);
        try
        {
            var fullArgs = BuildArguments(defaultsFile, request, includeCompatibilityFlags: true);
            var result = await RunDumpAsync(fullArgs, request.GzipOutputPath, cancellationToken);
            if (result.ExitCode != 0 && result.Error.Contains("unknown", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(request.GzipOutputPath);
                result = await RunDumpAsync(BuildArguments(defaultsFile, request, includeCompatibilityFlags: false), request.GzipOutputPath, cancellationToken);
            }

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"mysqldump failed: {SecretRedactor.Clean(result.Error)}");
            }

            var gzipBytes = new FileInfo(request.GzipOutputPath).Length;
            return new DumpResult(result.BytesWritten, gzipBytes, null);
        }
        finally
        {
            ClientFiles.DeleteQuietly(defaultsFile);
        }
    }

    private List<string> BuildArguments(string defaultsFile, DumpRequest request, bool includeCompatibilityFlags)
    {
        var args = new List<string> { $"--defaults-extra-file={defaultsFile}" };
        if (request.SingleTransaction)
        {
            args.Add("--single-transaction");
            args.Add("--skip-lock-tables");
        }

        args.Add("--no-tablespaces");
        args.Add("--default-character-set=utf8mb4");
        args.Add("--hex-blob");
        if (includeCompatibilityFlags)
        {
            args.Add("--column-statistics=0");
            args.Add("--set-gtid-purged=OFF");
        }

        if (request.Routines) args.Add("--routines");
        if (request.Events) args.Add("--events");
        if (!request.Triggers) args.Add("--skip-triggers");
        args.Add(request.DatabaseName);
        return args;
    }

    private async Task<(int ExitCode, string Error, long BytesWritten)> RunDumpAsync(List<string> args, string gzipPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_options.Value.MySqlDumpPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("mysqldump could not be started. Install the MySQL client tools and set Backup:MySqlDumpPath.");
        await using var registration = cancellationToken.Register(() => TryKill(process));
        await using var file = new FileStream(gzipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardOutput.BaseStream.CopyToAsync(gzip, cancellationToken);
        await gzip.FlushAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await stderr;
        return (process.ExitCode, error, file.Length);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}

public sealed class MySqlImportService : IMySqlImportService
{
    private readonly IOptions<BackupOptions> _options;
    public MySqlImportService(IOptions<BackupOptions> options) => _options = options;

    public async Task ImportAsync(MySqlEndpoint endpoint, string databaseName, Stream sqlStream, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (endpoint.Purpose != ConnectionPurpose.DestinationWrite)
        {
            throw new SourceSafetyException("Import can only target a destination credential.");
        }

        DatabaseNameRules.RequireSafe(databaseName);
        var directory = Path.Combine(Path.GetTempPath(), "mysql-manager-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var defaultsFile = await ClientFiles.WriteDefaultsAsync(directory, endpoint, cancellationToken);
        try
        {
            var start = new ProcessStartInfo(_options.Value.MySqlClientPath)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add($"--defaults-extra-file={defaultsFile}");
            start.ArgumentList.Add("--batch");
            start.ArgumentList.Add(databaseName);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("mysql client could not be started. Install the MySQL client tools and set Backup:MySqlClientPath.");
            await using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
            });
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            var buffer = new byte[1024 * 128];
            long transferred = 0;
            while (true)
            {
                var read = await sqlStream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await process.StandardInput.BaseStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                transferred += read;
                progress?.Report(transferred);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            var error = await stderr;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"mysql import failed: {SecretRedactor.Clean(error)}");
            }
        }
        finally
        {
            ClientFiles.DeleteQuietly(defaultsFile);
            try { Directory.Delete(directory, true); } catch { }
        }
    }
}

public sealed class GzipCompressionService : ICompressionService
{
    public Stream OpenDecompress(Stream gzipStream) => new GZipStream(gzipStream, CompressionMode.Decompress, leaveOpen: false);
}

internal static class ClientFiles
{
    public static async Task<string> WriteDefaultsAsync(string directory, MySqlEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (endpoint.Password.Contains('\n') || endpoint.Password.Contains('\r') || endpoint.Username.Contains('\n'))
        {
            throw new SourceSafetyException("Credentials containing line breaks are rejected.");
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "client.cnf");
        var builder = new StringBuilder();
        builder.AppendLine("[client]");
        builder.AppendLine(Quote("user", endpoint.Username));
        builder.AppendLine(Quote("password", endpoint.Password));
        builder.AppendLine(Quote("host", endpoint.Host));
        builder.AppendLine($"port={endpoint.Port}");
        builder.AppendLine("protocol=tcp");
        if (endpoint.SslEnabled)
        {
            if (!string.IsNullOrWhiteSpace(endpoint.SslCertificatePem))
            {
                var ca = Path.Combine(directory, "ca.pem");
                await File.WriteAllTextAsync(ca, endpoint.SslCertificatePem, cancellationToken);
                builder.AppendLine(Quote("ssl-ca", ca.Replace('\\', '/')));
            }

            builder.AppendLine("ssl=1");
        }
        else
        {
            builder.AppendLine("ssl=0");
        }

        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken);
        return path;
    }

    public static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var ca = Path.Combine(Path.GetDirectoryName(path)!, "ca.pem");
            if (File.Exists(ca))
            {
                File.Delete(ca);
            }
        }
        catch
        {
        }
    }

    private static string Quote(string key, string value) => $"{key}=\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
