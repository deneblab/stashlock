using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Deneblab.StashLock.Cli.Modules.Ecies;
using Deneblab.StashLock.Client.Common;
using Xunit;
using CliSopsEncryptedFile = Deneblab.StashLock.Cli.Modules.Encode.SopsEncryptedFile;
using CliSopsMetadata = Deneblab.StashLock.Cli.Modules.Encode.SopsMetadata;
using CliStashlockEncryptedFile = Deneblab.StashLock.Cli.Modules.Encode.StashlockEncryptedFile;
using CliAesGcmHelper = Deneblab.StashLock.Cli.Modules.Ecies.AesGcmHelper;

namespace Deneblab.StashLock.Tests.Cli;

/// <summary>
///     Cross-compatibility tests: encrypt with CLI code, decrypt with Client code.
/// </summary>
public class ClientCliCompatibilityTests
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [Fact]
    public void EciesDecryptor_ShouldDecryptCliSealedMessage()
    {
        // Encrypt with CLI's EciesModule
        var keyPair = EciesModule.GenerateKeyPair("test");
        var plaintext = Encoding.UTF8.GetBytes("Hello from CLI!");
        var sealed_ = EciesModule.Seal(plaintext, keyPair.GetPublicKeyBytes());

        // Decrypt with Client's EciesDecryptor
        var decrypted = EciesDecryptor.Open(sealed_, keyPair.GetPrivateKeyBytes());

        Assert.Equal("Hello from CLI!", Encoding.UTF8.GetString(decrypted));
    }

    [Fact]
    public void EciesDecryptor_ShouldFailWithWrongKey()
    {
        var keyPair = EciesModule.GenerateKeyPair("right");
        var wrongKey = EciesModule.GenerateKeyPair("wrong");
        var plaintext = Encoding.UTF8.GetBytes("secret");
        var sealed_ = EciesModule.Seal(plaintext, keyPair.GetPublicKeyBytes());

        Assert.ThrowsAny<Exception>(() =>
            EciesDecryptor.Open(sealed_, wrongKey.GetPrivateKeyBytes()));
    }

    [Fact]
    public void SopsMode_ClientDecryptsCliEncrypted()
    {
        var keyPair = EciesModule.GenerateKeyPair("prod");
        var secrets = new Dictionary<string, string>
        {
            ["db_host"] = "prod-server.example.com",
            ["api_key"] = "sk-abc123",
            ["port"] = "5432"
        };

        // Encrypt with CLI code
        var sopsFile = GenerateSops(secrets, keyPair, "myapp.prod.00001");
        var sopsJson = JsonSerializer.Serialize(sopsFile, WriteOptions);

        // Decrypt with Client code
        var decrypted = SopsDecryptor.DecryptAutoDetect(sopsJson, keyPair.GetPrivateKeyBytes());

        Assert.Equal(secrets.Count, decrypted.Count);
        foreach (var kvp in secrets)
            Assert.Equal(kvp.Value, decrypted[kvp.Key]);
    }

    [Fact]
    public void SopsMode_ClientDecryptsCliEncrypted_WithUnicode()
    {
        var keyPair = EciesModule.GenerateKeyPair("test");
        var secrets = new Dictionary<string, string>
        {
            ["greeting"] = "hélllo wörld! 日本語 🔐",
            ["empty"] = "",
            ["special"] = "a=b&c=d?e#f"
        };

        var sopsFile = GenerateSops(secrets, keyPair, "app.test.00001");
        var sopsJson = JsonSerializer.Serialize(sopsFile, WriteOptions);

        var decrypted = SopsDecryptor.DecryptAutoDetect(sopsJson, keyPair.GetPrivateKeyBytes());

        foreach (var kvp in secrets)
            Assert.Equal(kvp.Value, decrypted[kvp.Key]);
    }

    [Fact]
    public void WholeFileMode_ClientDecryptsCliEncrypted()
    {
        var keyPair = EciesModule.GenerateKeyPair("test");
        var originalJson = "{\"db\":\"server=prod\",\"key\":\"abc123\"}";

        // Encrypt with CLI code
        var plaintextBytes = Encoding.UTF8.GetBytes(originalJson);
        var sealedBytes = EciesModule.Seal(plaintextBytes, keyPair.GetPublicKeyBytes());
        var encFile = new CliStashlockEncryptedFile
        {
            VaultKey = "app.test.00001",
            EncryptedData = Convert.ToBase64String(sealedBytes)
        };
        var encJson = JsonSerializer.Serialize(encFile, WriteOptions);

        // Decrypt with Client code
        var decrypted = SopsDecryptor.DecryptAutoDetect(encJson, keyPair.GetPrivateKeyBytes());

        Assert.Equal("server=prod", decrypted["db"]);
        Assert.Equal("abc123", decrypted["key"]);
    }

    [Fact]
    public void SopsMode_ClientFailsWithWrongKey()
    {
        var keyPair = EciesModule.GenerateKeyPair("right");
        var wrongKey = EciesModule.GenerateKeyPair("wrong");
        var secrets = new Dictionary<string, string> { ["secret"] = "value" };

        var sopsFile = GenerateSops(secrets, keyPair, "app.right.00001");
        var sopsJson = JsonSerializer.Serialize(sopsFile, WriteOptions);

        Assert.ThrowsAny<Exception>(() =>
            SopsDecryptor.DecryptAutoDetect(sopsJson, wrongKey.GetPrivateKeyBytes()));
    }

    [Fact]
    public void SopsMode_ClientDetectsTamperedMAC()
    {
        var keyPair = EciesModule.GenerateKeyPair("test");
        var secrets = new Dictionary<string, string> { ["key"] = "value" };

        var sopsFile = GenerateSops(secrets, keyPair, "app.test.00001");

        // Tamper with a value
        var originalValue = sopsFile.Values["key"];
        sopsFile.Values["key"] = "ENC[AesGcm:AAAA" + originalValue.Substring("ENC[AesGcm:AAAA".Length);

        var sopsJson = JsonSerializer.Serialize(sopsFile, WriteOptions);

        Assert.ThrowsAny<Exception>(() =>
            SopsDecryptor.DecryptAutoDetect(sopsJson, keyPair.GetPrivateKeyBytes()));
    }

    // --- Helper: mirrors CLI's encode logic ---

    private static CliSopsEncryptedFile GenerateSops(
        Dictionary<string, string> secrets, KeyPairModel keyPair, string vaultKey)
    {
        var dataKey = CliAesGcmHelper.GenerateDataKey();
        var encryptedValues = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in secrets)
            encryptedValues[kvp.Key] = CliAesGcmHelper.EncryptValue(dataKey, kvp.Value);

        var mac = CliAesGcmHelper.ComputeMac(dataKey, encryptedValues);
        var macEncrypted = CliAesGcmHelper.EncryptValue(dataKey, Convert.ToBase64String(mac));
        var wrappedDataKey = EciesModule.Seal(dataKey, keyPair.GetPublicKeyBytes());

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(dataKey);

        return new CliSopsEncryptedFile
        {
            Values = new Dictionary<string, string>(encryptedValues),
            Metadata = new CliSopsMetadata
            {
                VaultKey = vaultKey,
                Mode = "sops",
                DataKey = Convert.ToBase64String(wrappedDataKey),
                Mac = macEncrypted
            }
        };
    }
}
