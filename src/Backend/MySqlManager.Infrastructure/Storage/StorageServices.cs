using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage.Blobs;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Configuration;
using MySqlManager.Domain.Safety;
using Microsoft.Extensions.Options;

namespace MySqlManager.Infrastructure.Storage;

public sealed class LocalFileStorage : IBackupStorage
{
    private readonly string _root;

    public LocalFileStorage(IOptions<BackupOptions> options)
    {
        _root = Path.GetFullPath(options.Value.RootPath);
        Directory.CreateDirectory(_root);
    }

    public string RootPath => _root;

    public DiskUsage GetDiskUsage()
    {
        var root = Path.GetPathRoot(_root);
        if (string.IsNullOrWhiteSpace(root))
        {
            return new DiskUsage(0, 0, 0);
        }

        var drive = new DriveInfo(root);
        var free = drive.AvailableFreeSpace;
        var total = drive.TotalSize;
        var percent = total == 0 ? 0 : free * 100d / total;
        return new DiskUsage(free, total, percent);
    }

    public string Promote(string temporaryFile, string relativeDirectory, string fileName)
    {
        var directory = BackupPathRules.EnsureInsideRoot(_root, Path.Combine(_root, relativeDirectory));
        Directory.CreateDirectory(directory);
        var destination = BackupPathRules.EnsureInsideRoot(_root, Path.Combine(directory, BackupPathRules.SanitizeSegment(Path.GetFileNameWithoutExtension(fileName)) + Path.GetExtension(fileName)));
        if (Path.GetExtension(fileName).Equals(".enc", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".sql.gz.enc", StringComparison.OrdinalIgnoreCase))
        {
            destination = BackupPathRules.EnsureInsideRoot(_root, Path.Combine(directory, Path.GetFileName(fileName)));
        }

        File.Move(temporaryFile, destination, overwrite: false);
        return destination;
    }

    public Stream OpenRead(string storagePath)
    {
        var full = BackupPathRules.EnsureInsideRoot(_root, storagePath);
        return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
    }

    public void Delete(string storagePath)
    {
        var full = BackupPathRules.EnsureInsideRoot(_root, storagePath);
        if (File.Exists(full))
        {
            File.Delete(full);
        }
    }
}

public sealed class S3BackupStorage : IBackupStorage
{
    private readonly LocalFileStorage _local;
    private readonly IOptions<StorageOptions> _options;

    public S3BackupStorage(LocalFileStorage local, IOptions<StorageOptions> options)
    {
        _local = local;
        _options = options;
    }

    public string RootPath => _local.RootPath;
    public DiskUsage GetDiskUsage() => _local.GetDiskUsage();

    public string Promote(string temporaryFile, string relativeDirectory, string fileName)
    {
        var path = _local.Promote(temporaryFile, relativeDirectory, fileName);
        Upload(path);
        return path;
    }

    public Stream OpenRead(string storagePath) => _local.OpenRead(storagePath);

    public void Delete(string storagePath)
    {
        _local.Delete(storagePath);
        if (string.IsNullOrWhiteSpace(_options.Value.S3Bucket))
        {
            return;
        }

        using var client = new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(_options.Value.S3Region));
        client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _options.Value.S3Bucket, Key = storagePath }).GetAwaiter().GetResult();
    }

    private void Upload(string path)
    {
        if (string.IsNullOrWhiteSpace(_options.Value.S3Bucket))
        {
            throw new InvalidOperationException("Storage:S3Bucket is required when Storage:Type is S3.");
        }

        using var client = new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(_options.Value.S3Region));
        using var stream = File.OpenRead(path);
        client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Value.S3Bucket,
            Key = path,
            InputStream = stream,
            AutoCloseStream = false
        }).GetAwaiter().GetResult();
    }
}

public sealed class AzureBlobBackupStorage : IBackupStorage
{
    private readonly LocalFileStorage _local;
    private readonly IOptions<StorageOptions> _options;

    public AzureBlobBackupStorage(LocalFileStorage local, IOptions<StorageOptions> options)
    {
        _local = local;
        _options = options;
    }

    public string RootPath => _local.RootPath;
    public DiskUsage GetDiskUsage() => _local.GetDiskUsage();

    public string Promote(string temporaryFile, string relativeDirectory, string fileName)
    {
        var path = _local.Promote(temporaryFile, relativeDirectory, fileName);
        var container = Client();
        var blob = container.GetBlobClient(Path.GetFileName(path));
        using var stream = File.OpenRead(path);
        blob.Upload(stream, overwrite: false);
        return path;
    }

    public Stream OpenRead(string storagePath) => _local.OpenRead(storagePath);

    public void Delete(string storagePath)
    {
        _local.Delete(storagePath);
        Client().GetBlobClient(Path.GetFileName(storagePath)).DeleteIfExists();
    }

    private BlobContainerClient Client()
    {
        if (string.IsNullOrWhiteSpace(_options.Value.AzureBlobConnectionString))
        {
            throw new InvalidOperationException("Storage:AzureBlobConnectionString is required when Storage:Type is AzureBlob.");
        }

        var service = new BlobServiceClient(_options.Value.AzureBlobConnectionString);
        var container = service.GetBlobContainerClient(_options.Value.AzureBlobContainer);
        container.CreateIfNotExists();
        return container;
    }
}
