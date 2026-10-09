using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Common;
using MySqlManager.Application.Configuration;
using MySqlManager.Domain.Enums;
using MySqlManager.Domain.Safety;

namespace MySqlManager.Infrastructure.Git;

public sealed class GitCliService : IGitService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> WorkspaceGates = new();

    private readonly IOptions<BackupOptions> _backup;
    private readonly IOptions<GitOptions> _git;

    public GitCliService(IOptions<BackupOptions> backup, IOptions<GitOptions> git)
    {
        _backup = backup;
        _git = git;
    }

    public async Task<GitConnectionReport> TestAsync(GitSyncWorkItem item, CancellationToken cancellationToken)
    {
        try
        {
            var output = await RunAsync(null, ["ls-remote", item.RemoteUrl, "HEAD"], item, cancellationToken);
            var sha = output.Split(['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return new GitConnectionReport(item.RemoteUrl, item.Branch, true, null, sha, null);
        }
        catch (Exception ex)
        {
            return new GitConnectionReport(item.RemoteUrl, item.Branch, false, null, null, SecretRedactor.Clean(ex.Message));
        }
    }

    public async Task<string?> SyncAsync(GitSyncWorkItem item, CancellationToken cancellationToken)
    {
        if (!_git.Value.Enabled)
        {
            return null;
        }

        var work = await PrepareWorkspaceAsync(item, cancellationToken);
        var databaseDir = Path.Combine(work, item.ServerFolder, item.DatabaseFolder, "daily");
        Directory.CreateDirectory(databaseDir);
        if (item.Mode != GitMode.MetadataOnly && !string.IsNullOrWhiteSpace(item.BackupFilePath) && !string.IsNullOrWhiteSpace(item.BackupFileName))
        {
            var target = BackupPathRules.EnsureInsideRoot(work, Path.Combine(databaseDir, Path.GetFileName(item.BackupFileName)));
            File.Copy(item.BackupFilePath, target, overwrite: true);
        }

        if (item.Mode == GitMode.BackupFilesWithLfs)
        {
            await RunAsync(work, ["lfs", "install", "--local"], item, cancellationToken);
            await RunAsync(work, ["lfs", "track", "*.sql.gz.enc"], item, cancellationToken);
        }

        var manifestPath = Path.Combine(work, item.ServerFolder, item.DatabaseFolder, "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await File.WriteAllTextAsync(manifestPath, item.ManifestJson, cancellationToken);
        if (Directory.Exists(databaseDir))
        {
            foreach (var file in Directory.EnumerateFiles(databaseDir))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".sql.gz.enc", StringComparison.OrdinalIgnoreCase) && !item.RetainedFileNames.Contains(name))
                {
                    await RunAsync(work, ["rm", "-f", "--", Relative(work, file)], item, cancellationToken);
                }
            }
        }

        await RunAsync(work, ["add", "-A"], item, cancellationToken);
        var status = await RunAsync(work, ["status", "--porcelain"], item, cancellationToken);
        if (string.IsNullOrWhiteSpace(status))
        {
            return await RunAsync(work, ["rev-parse", "HEAD"], item, cancellationToken);
        }

        await RunAsync(work, ["-c", "user.email=backup@mysql-manager.local", "-c", "user.name=MySQL Manager", "commit", "-m", $"backup {item.DatabaseFolder} {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}"], item, cancellationToken);
        await RunAsync(work, ["push", "origin", $"HEAD:{item.Branch}"], item, cancellationToken);
        return (await RunAsync(work, ["rev-parse", "HEAD"], item, cancellationToken)).Trim();
    }

    public async Task PurgeHistoryAsync(GitSyncWorkItem item, CancellationToken cancellationToken)
    {
        if (!GitHistoryPolicy.CanPurge(item.AllowHistoryRewrite, _git.Value.AllowHistoryRewrite))
        {
            throw new InvalidOperationException("Git history rewrite is disabled. Older backup versions remain recoverable from history.");
        }

        var work = await PrepareWorkspaceAsync(item, cancellationToken);
        await RunAsync(work, ["checkout", "--orphan", "msm-rolling-temp"], item, cancellationToken);
        await RunAsync(work, ["add", "-A"], item, cancellationToken);
        await RunAsync(work, ["-c", "user.email=backup@mysql-manager.local", "-c", "user.name=MySQL Manager", "commit", "-m", "rolling backup history purge"], item, cancellationToken);
        await RunAsync(work, ["branch", "-M", item.Branch], item, cancellationToken);
        await RunAsync(work, ["push", "--force", "origin", item.Branch], item, cancellationToken);
    }

    public async Task<IReadOnlyList<GitLogEntry>> GetLogAsync(GitSyncWorkItem item, CancellationToken cancellationToken)
    {
        var work = await PrepareWorkspaceAsync(item, cancellationToken);
        string output;
        try
        {
            output = await RunAsync(work, ["log", "-n", "30", "--pretty=format:%H%x09%cI%x09%s"], item, cancellationToken);
        }
        catch (InvalidOperationException ex) when (HasNoCommits(ex))
        {
            var branches = await ListRemoteBranchesAsync(item, cancellationToken);
            if (branches.Count > 0 && !branches.Contains(item.Branch, StringComparer.Ordinal))
            {
                throw new ConflictException("GIT_BRANCH_MISSING", $"Branch {item.Branch} has no commits. The remote has: {string.Join(", ", branches)}.");
            }

            return [];
        }

        var entries = new List<GitLogEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || !DateTime.TryParse(parts[1], out var when))
            {
                continue;
            }

            entries.Add(new GitLogEntry(parts[0], when.ToUniversalTime(), parts[2]));
        }

        return entries;
    }

    private async Task<string> PrepareWorkspaceAsync(GitSyncWorkItem item, CancellationToken cancellationToken)
    {
        var gate = WorkspaceGates.GetOrAdd(item.RepositoryId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var root = Path.GetFullPath(_backup.Value.GitWorkspacePath);
            Directory.CreateDirectory(root);
            var work = BackupPathRules.EnsureInsideRoot(root, Path.Combine(root, item.RepositoryId.ToString("D")));
            if (!Directory.Exists(Path.Combine(work, ".git")))
            {
                await DeleteDirectoryAsync(work, cancellationToken);
                try
                {
                    await RunAsync(root, ["clone", "--branch", item.Branch, "--single-branch", item.RemoteUrl, work], item, cancellationToken);
                }
                catch (InvalidOperationException ex) when (IsMissingRemoteBranch(ex))
                {
                    await DeleteDirectoryAsync(work, cancellationToken);
                    Directory.CreateDirectory(work);
                    await RunAsync(work, ["init", "-b", item.Branch], item, cancellationToken);
                    await RunAsync(work, ["remote", "add", "origin", item.RemoteUrl], item, cancellationToken);
                }
            }
            else
            {
                try
                {
                    await RunAsync(work, ["pull", "--ff-only", "origin", item.Branch], item, cancellationToken);
                }
                catch (InvalidOperationException ex) when (IsMissingRemoteBranch(ex))
                {
                }
            }

            return work;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> ListRemoteBranchesAsync(GitSyncWorkItem item, CancellationToken cancellationToken)
    {
        var output = await RunAsync(null, ["ls-remote", "--heads", item.RemoteUrl], item, cancellationToken);
        var names = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0)
            {
                continue;
            }

            const string prefix = "refs/heads/";
            var reference = line[(tab + 1)..].Trim();
            if (reference.StartsWith(prefix, StringComparison.Ordinal))
            {
                names.Add(reference[prefix.Length..]);
            }
        }

        return names;
    }

    private static bool IsMissingRemoteBranch(Exception ex) =>
        ex.Message.Contains("Remote branch", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("no tracking information", StringComparison.OrdinalIgnoreCase);

    private static bool HasNoCommits(Exception ex) =>
        ex.Message.Contains("does not have any commits", StringComparison.OrdinalIgnoreCase);

    private static async Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(200, cancellationToken);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                await Task.Delay(200, cancellationToken);
            }
        }
    }

    private async Task<string> RunAsync(string? workingDirectory, IReadOnlyList<string> args, GitSyncWorkItem item, CancellationToken cancellationToken)
    {
        if (item.RemoteUrl.Contains('\n') || item.RemoteUrl.Contains('\r') || item.Branch.Contains(' '))
        {
            throw new SourceSafetyException("Git remote URL or branch is invalid.");
        }

        var start = new ProcessStartInfo(_backup.Value.GitPath)
        {
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        var keyPath = ApplyAuth(start, item);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("git could not be started.");
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(SecretRedactor.Clean(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr));
            }

            return stdout;
        }
        finally
        {
            if (keyPath is not null)
            {
                try { File.Delete(keyPath); } catch (IOException) { }
            }
        }
    }

    private static string? ApplyAuth(ProcessStartInfo start, GitSyncWorkItem item)
    {
        if (string.IsNullOrEmpty(item.Secret))
        {
            return null;
        }

        if (item.AuthType == GitAuthType.HttpsPat)
        {
            if (!Uri.TryCreate(item.RemoteUrl, UriKind.Absolute, out var remote) || remote.Scheme != Uri.UriSchemeHttps)
            {
                throw new SourceSafetyException("HTTPS token authentication requires an https remote URL.");
            }

            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{item.Secret}"));
            // A global http.extraheader is also sent to Git LFS storage on S3, which rejects it.
            var origin = $"{remote.Scheme}://{remote.Host}/";
            start.Environment["GIT_CONFIG_COUNT"] = "1";
            start.Environment["GIT_CONFIG_KEY_0"] = $"http.{origin}.extraheader";
            start.Environment["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: Basic {basic}";
            return null;
        }

        var keyPath = Path.Combine(Path.GetTempPath(), $"msm-git-{Guid.NewGuid():N}");
        File.WriteAllText(keyPath, item.Secret);
        start.Environment["GIT_SSH_COMMAND"] = $"ssh -i \"{keyPath}\" -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new";
        return keyPath;
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
