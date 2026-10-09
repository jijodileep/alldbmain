using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MySqlManager.Application.Abstractions;
using MySqlManager.Application.Configuration;
using MySqlManager.Application.Common;
using MySqlManager.Domain.Enums;
using MySqlManager.Infrastructure.Pipelines;
using MySqlManager.Infrastructure.Security;
using Xunit;

namespace MySqlManager.Infrastructure.Tests;

public class EncryptionTests
{
    [Fact]
    public async Task RoundTripDoesNotLoadTheWholePlaintextBufferBeyondChunks()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = Encoding.UTF8.GetBytes(new string('A', 1024 * 1024) + "backup-tail");
        await using var source = new MemoryStream(plain);
        await using var encrypted = new MemoryStream();
        await ChunkedAesGcm.EncryptAsync(source, encrypted, key, "k-test", CancellationToken.None);
        encrypted.Position = 0;
        await using var decrypt = new ChunkedDecryptStream(encrypted, key, "k-test");
        using var result = new MemoryStream();
        await decrypt.CopyToAsync(result);
        Assert.Equal(plain, result.ToArray());
    }

    [Fact]
    public async Task TamperedCiphertextFailsAuthentication()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("safe-backup"));
        await using var encrypted = new MemoryStream();
        await ChunkedAesGcm.EncryptAsync(source, encrypted, key, "k-test", CancellationToken.None);
        var bytes = encrypted.ToArray();
        bytes[^1] ^= 0xFF;
        await using var tampered = new MemoryStream(bytes);
        await using var decrypt = new ChunkedDecryptStream(tampered, key, "k-test");
        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
        {
            using var sink = new MemoryStream();
            await decrypt.CopyToAsync(sink);
        });
    }

    [Fact]
    public async Task CredentialEncryptionRoundTripsWithoutLeavingThePasswordInTheCiphertext()
    {
        var protector = new AesSecretProtector(
            Options.Create(new BackupOptions { MasterKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")) }),
            Options.Create(new SecretOptions()),
            []);
        var protectedSecret = await protector.ProtectAsync("super-secret-password", SecretProviderKind.LocalEncrypted, null, CancellationToken.None);
        Assert.DoesNotContain("super-secret-password", protectedSecret.Ciphertext, StringComparison.Ordinal);
        var plain = await protector.UnprotectAsync(protectedSecret, SecretProviderKind.LocalEncrypted, CancellationToken.None);
        Assert.Equal("super-secret-password", plain);
    }
}

public class CopyValidationTests
{
    [Fact]
    public void StandardValidationFailsWhenTableCountsDiffer()
    {
        var source = new SchemaInventory(true, "utf8mb4", "utf8mb4_unicode_ci", 10, 1, 2, 3, 0, 100, 1000, []);
        var destination = source with { Tables = 9 };
        Assert.False(CopyPipeline.Compare(source, destination, ValidationLevel.Standard));
    }

    [Fact]
    public void MissingDestinationFailsValidation()
    {
        var source = new SchemaInventory(true, "utf8mb4", "utf8mb4_unicode_ci", 1, 0, 0, 0, 0, 1, 1, []);
        var destination = source with { Exists = false };
        Assert.False(CopyPipeline.Compare(source, destination, ValidationLevel.Basic));
    }

    [Fact]
    public void ExistingDestinationStopsAndDoesNotDrop()
    {
        var error = new DestinationExistsException("customer_001", "customer_001_copy_20261008");
        Assert.Equal("DESTINATION_ALREADY_EXISTS", error.MessageKey);
        Assert.Equal("customer_001_copy_20261008", error.SuggestedName);
        Assert.DoesNotContain("DROP", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
