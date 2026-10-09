using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Configuration;
using MySqlManager.Application.Security;
using MySqlManager.Domain.Backups;
using MySqlManager.Domain.Enums;

namespace MySqlManager.Infrastructure.Security;

public sealed class AesSecretProtector : ISecretProtector
{
    private readonly IOptions<BackupOptions> _backup;
    private readonly IOptions<SecretOptions> _secrets;
    private readonly IReadOnlyDictionary<SecretProviderKind, IExternalSecretProvider> _providers;

    public AesSecretProtector(IOptions<BackupOptions> backup, IOptions<SecretOptions> secrets, IEnumerable<IExternalSecretProvider> providers)
    {
        _backup = backup;
        _secrets = secrets;
        _providers = providers.ToDictionary(p => p.Kind);
    }

    public async Task<ProtectedSecret> ProtectAsync(string plaintext, SecretProviderKind provider, string? externalName, CancellationToken cancellationToken)
    {
        if (provider != SecretProviderKind.LocalEncrypted)
        {
            if (string.IsNullOrWhiteSpace(externalName))
            {
                throw new InvalidOperationException("An external secret name is required.");
            }

            return new ProtectedSecret(string.Empty, string.Empty, provider.ToString(), externalName);
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[Encoding.UTF8.GetByteCount(plaintext)];
        var tag = new byte[16];
        using var aes = new AesGcm(MasterKey(_backup.Value), 16);
        aes.Encrypt(nonce, Encoding.UTF8.GetBytes(plaintext), cipher, tag);
        return new ProtectedSecret(Convert.ToBase64String(cipher.Concat(tag).ToArray()), Convert.ToBase64String(nonce), "master", null);
    }

    public async Task<string> UnprotectAsync(ProtectedSecret secret, SecretProviderKind provider, CancellationToken cancellationToken)
    {
        if (provider != SecretProviderKind.LocalEncrypted)
        {
            if (!_providers.TryGetValue(provider, out var external))
            {
                throw new InvalidOperationException($"Secret provider {provider} is not registered.");
            }

            return await external.GetAsync(secret.ExternalName ?? string.Empty, cancellationToken);
        }

        var nonce = Convert.FromBase64String(secret.Nonce);
        var payload = Convert.FromBase64String(secret.Ciphertext);
        var cipher = payload[..^16];
        var tag = payload[^16..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(MasterKey(_backup.Value), 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    internal static byte[] MasterKey(BackupOptions options)
    {
        var key = Convert.FromBase64String(options.MasterKeyBase64);
        if (key.Length != 32)
        {
            throw new InvalidOperationException("Backup:MasterKeyBase64 must decode to 32 bytes.");
        }

        return key;
    }
}

public interface IExternalSecretProvider
{
    SecretProviderKind Kind { get; }
    Task<string> GetAsync(string name, CancellationToken cancellationToken);
}

public sealed class EnvironmentSecretProvider : IExternalSecretProvider
{
    public SecretProviderKind Kind => SecretProviderKind.Environment;
    public Task<string> GetAsync(string name, CancellationToken cancellationToken)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"Environment secret '{name}' is not set.");
        }

        return Task.FromResult(value);
    }
}

public sealed class AzureKeyVaultSecretProvider : IExternalSecretProvider
{
    private readonly IOptions<SecretOptions> _options;
    public AzureKeyVaultSecretProvider(IOptions<SecretOptions> options) => _options = options;
    public SecretProviderKind Kind => SecretProviderKind.AzureKeyVault;

    public async Task<string> GetAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Value.AzureKeyVaultUri))
        {
            throw new InvalidOperationException("Secrets:AzureKeyVaultUri is not configured.");
        }

        var client = new Azure.Security.KeyVault.Secrets.SecretClient(new Uri(_options.Value.AzureKeyVaultUri), new Azure.Identity.DefaultAzureCredential());
        var secret = await client.GetSecretAsync(name, cancellationToken: cancellationToken);
        return secret.Value.Value;
    }
}

public sealed class AwsSecretsManagerProvider : IExternalSecretProvider
{
    private readonly IOptions<SecretOptions> _options;
    public AwsSecretsManagerProvider(IOptions<SecretOptions> options) => _options = options;
    public SecretProviderKind Kind => SecretProviderKind.AwsSecretsManager;

    public async Task<string> GetAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Value.AwsRegion))
        {
            throw new InvalidOperationException("Secrets:AwsRegion is not configured.");
        }

        using var client = new Amazon.SecretsManager.AmazonSecretsManagerClient(Amazon.RegionEndpoint.GetBySystemName(_options.Value.AwsRegion));
        var response = await client.GetSecretValueAsync(new Amazon.SecretsManager.Model.GetSecretValueRequest { SecretId = name }, cancellationToken);
        return response.SecretString;
    }
}

public sealed class PasswordHashService : IPasswordHasher
{
    private readonly Microsoft.AspNetCore.Identity.PasswordHasher<Domain.Identity.User> _hasher = new();
    public string Hash(Domain.Identity.User user, string password) => _hasher.HashPassword(user, password);
    public bool Verify(Domain.Identity.User user, string passwordHash, string password) =>
        _hasher.VerifyHashedPassword(user, passwordHash, password) is Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success or Microsoft.AspNetCore.Identity.PasswordVerificationResult.SuccessRehashNeeded;
}

public sealed class JwtTokenService : ITokenService
{
    private readonly IOptions<JwtOptions> _options;
    private readonly IClock _clock;
    public JwtTokenService(IOptions<JwtOptions> options, IClock clock)
    {
        _options = options;
        _clock = clock;
    }

    public (string AccessToken, DateTime ExpiresAtUtc) CreateAccessToken(Domain.Identity.User user, IReadOnlyList<string> roles, IReadOnlyList<string> permissions)
    {
        var expires = _clock.UtcNow.AddMinutes(_options.Value.AccessTokenMinutes);
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(System.Security.Claims.ClaimTypes.Email, user.Email),
            new(System.Security.Claims.ClaimTypes.Name, user.DisplayName)
        };
        claims.AddRange(roles.Select(role => new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)));
        claims.AddRange(permissions.Select(permission => new System.Security.Claims.Claim("permission", permission)));
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Value.SigningKey));
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            _options.Value.Issuer,
            _options.Value.Audience,
            claims,
            notBefore: _clock.UtcNow,
            expires: expires,
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
        return (new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

public sealed class ChunkedEncryptionService : IEncryptionService, IKeyRotation
{
    private readonly IApplicationDbContext _db;
    private readonly IOptions<BackupOptions> _options;
    private readonly IClock _clock;

    public ChunkedEncryptionService(IApplicationDbContext db, IOptions<BackupOptions> options, IClock clock)
    {
        _db = db;
        _options = options;
        _clock = clock;
    }

    public async Task<string> EncryptFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var record = await GetActiveAsync(cancellationToken);
        var key = Unwrap(record);
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
        await ChunkedAesGcm.EncryptAsync(input, output, key, record.KeyId, cancellationToken);
        return record.KeyId;
    }

    public Stream OpenDecrypt(string encryptedPath, string keyId)
    {
        var record = _db.EncryptionKeys.AsNoTracking().First(k => k.KeyId == keyId);
        return new ChunkedDecryptStream(File.OpenRead(encryptedPath), Unwrap(record), keyId);
    }

    public async Task VerifyAsync(string encryptedPath, string keyId, string expectedSha256, CancellationToken cancellationToken)
    {
        var actual = await ComputeSha256Async(encryptedPath, cancellationToken);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Backup checksum verification failed.");
        }

        await using var stream = OpenDecrypt(encryptedPath, keyId);
        var buffer = new byte[1024 * 128];
        while (await stream.ReadAsync(buffer, cancellationToken) > 0)
        {
        }
    }

    public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    public async Task<string> RotateAsync(CancellationToken cancellationToken)
    {
        var existing = await _db.EncryptionKeys.Where(k => k.IsActive).ToListAsync(cancellationToken);
        foreach (var key in existing)
        {
            key.IsActive = false;
            key.RotatedAtUtc = _clock.UtcNow;
        }

        var created = CreateRecord();
        _db.EncryptionKeys.Add(created);
        await _db.SaveChangesAsync(cancellationToken);
        return created.KeyId;
    }

    private async Task<EncryptionKeyRecord> GetActiveAsync(CancellationToken cancellationToken)
    {
        var active = await _db.EncryptionKeys.FirstOrDefaultAsync(k => k.IsActive, cancellationToken);
        if (active is not null)
        {
            return active;
        }

        active = CreateRecord();
        _db.EncryptionKeys.Add(active);
        await _db.SaveChangesAsync(cancellationToken);
        return active;
    }

    private EncryptionKeyRecord CreateRecord()
    {
        var dataKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[dataKey.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(AesSecretProtector.MasterKey(_options.Value), 16);
        aes.Encrypt(nonce, dataKey, cipher, tag);
        return new EncryptionKeyRecord
        {
            KeyId = $"k-{_clock.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            WrappedKey = Convert.ToBase64String(cipher.Concat(tag).ToArray()),
            WrapNonce = Convert.ToBase64String(nonce),
            IsActive = true,
            CreatedAtUtc = _clock.UtcNow
        };
    }

    private byte[] Unwrap(EncryptionKeyRecord record)
    {
        var nonce = Convert.FromBase64String(record.WrapNonce);
        var payload = Convert.FromBase64String(record.WrappedKey);
        var cipher = payload[..^16];
        var tag = payload[^16..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(AesSecretProtector.MasterKey(_options.Value), 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}

public static class ChunkedAesGcm
{
    public static async Task EncryptAsync(Stream source, Stream destination, byte[] key, string keyId, CancellationToken cancellationToken)
    {
        var keyIdBytes = Encoding.UTF8.GetBytes(keyId);
        await destination.WriteAsync(Encoding.ASCII.GetBytes("MSMB"), cancellationToken);
        await destination.WriteAsync(BitConverter.GetBytes((ushort)1), cancellationToken);
        await destination.WriteAsync(BitConverter.GetBytes((ushort)keyIdBytes.Length), cancellationToken);
        await destination.WriteAsync(keyIdBytes, cancellationToken);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[read];
            var tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, buffer.AsSpan(0, read), cipher, tag);
            await destination.WriteAsync(nonce, cancellationToken);
            await destination.WriteAsync(tag, cancellationToken);
            await destination.WriteAsync(BitConverter.GetBytes(read), cancellationToken);
            await destination.WriteAsync(cipher, cancellationToken);
        }
    }
}

public sealed class ChunkedDecryptStream : Stream
{
    private readonly Stream _source;
    private readonly byte[] _key;
    private byte[] _plain = [];
    private int _offset;
    private bool _headerRead;

    public ChunkedDecryptStream(Stream source, byte[] key, string expectedKeyId)
    {
        _source = source;
        _key = key;
        ExpectedKeyId = expectedKeyId;
    }

    public string ExpectedKeyId { get; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_headerRead)
        {
            await ReadHeaderAsync(cancellationToken);
        }

        if (_offset >= _plain.Length && !await ReadFrameAsync(cancellationToken))
        {
            return 0;
        }

        var count = Math.Min(buffer.Length, _plain.Length - _offset);
        _plain.AsSpan(_offset, count).CopyTo(buffer.Span);
        _offset += count;
        return count;
    }

    private async Task ReadHeaderAsync(CancellationToken cancellationToken)
    {
        var magic = await ReadExactAsync(4, cancellationToken);
        if (Encoding.ASCII.GetString(magic) != "MSMB")
        {
            throw new InvalidDataException("Encrypted backup header is invalid.");
        }

        var version = BitConverter.ToUInt16(await ReadExactAsync(2, cancellationToken));
        if (version != 1)
        {
            throw new InvalidDataException("Unsupported backup encryption version.");
        }

        var keyLength = BitConverter.ToUInt16(await ReadExactAsync(2, cancellationToken));
        var keyId = Encoding.UTF8.GetString(await ReadExactAsync(keyLength, cancellationToken));
        if (!string.Equals(keyId, ExpectedKeyId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Encryption key id does not match the backup metadata.");
        }

        _headerRead = true;
    }

    private async Task<bool> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var nonceBuffer = new byte[12];
        var read = await _source.ReadAsync(nonceBuffer, cancellationToken);
        if (read == 0)
        {
            return false;
        }

        if (read < 12)
        {
            nonceBuffer = await CompleteAsync(nonceBuffer, read, 12, cancellationToken);
        }

        var tag = await ReadExactAsync(16, cancellationToken);
        var length = BitConverter.ToInt32(await ReadExactAsync(4, cancellationToken));
        if (length <= 0 || length > 2 * 1024 * 1024)
        {
            throw new InvalidDataException("Encrypted frame length is invalid.");
        }

        var cipher = await ReadExactAsync(length, cancellationToken);
        var plain = new byte[length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonceBuffer, cipher, tag, plain);
        _plain = plain;
        _offset = 0;
        return true;
    }

    private async Task<byte[]> ReadExactAsync(int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await _source.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Encrypted backup ended unexpectedly.");
            }

            offset += read;
        }

        return buffer;
    }

    private async Task<byte[]> CompleteAsync(byte[] start, int already, int needed, CancellationToken cancellationToken)
    {
        var rest = await ReadExactAsync(needed - already, cancellationToken);
        var full = new byte[needed];
        start.AsSpan(0, already).CopyTo(full);
        rest.CopyTo(full.AsSpan(already));
        return full;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Dispose();
        }

        base.Dispose(disposing);
    }
}
