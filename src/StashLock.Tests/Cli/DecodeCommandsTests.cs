using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Deneblab.StashLock.Cli.Modules.Ecies;
using Deneblab.StashLock.Cli.Modules.Encode;
using Xunit;

namespace Deneblab.StashLock.Tests.Cli;

public class DecodeCommandsTests
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [Fact]
    public void SopsMode_RoundTrip_ShouldDecryptToOriginalValues()
    {
        var keyPair = EciesModule.GenerateKeyPair("test");
        var secrets = new Dictionary<string, string>
        {
            ["db_host"] = "prod-server.example.com",
            ["api_key"] = "sk-abc123",
            ["port"] = "5432"
        };

        var encrypted = GenerateSops(secrets, keyPair, "myapp.test.00001");
        var decrypted = DecodeSops(encrypted, keyPair);

        Assert.Equal(secrets.Count, decrypted.Count);
        foreach (var kvp in secrets)
            Assert.Equal(kvp.Value, decrypted[kvp.Key]);
    }

    [Fact]
    public void SopsMode_RoundTrip_WithUnicodeValues()
    {
        var keyPair = EciesModule.GenerateKeyPair("test");
        var secrets = new Dictionary<string, string>
        {
            ["greeting"] = "hélllo wörld! 日本語 🔐",
            ["empty"] = "",
            ["special"] = "a=b&c=d?e#f"
        };

        var encrypted = GenerateSops(secrets, keyPair, "app.test.00001");
        var decrypted = DecodeSops(encrypted, keyPair);

        foreach (var kvp in secrets)
            Assert.Equal(kvp.Value, decrypted[kvp.Key]);
    }

    [Fact]
    public void SopsMode_ShouldFailWithWrongKey()
    {
        var keyPair = EciesModule.GenerateKeyPair("right");
        var wrongKey = EciesModule.GenerateKeyPair("wrong");
        var secrets = new Dictionary<string, string> { ["secret"] = "value" };

        var encrypted = GenerateSops(secrets, keyPair, "app.right.00001");

        Assert.ThrowsAny<Exception>(() => DecodeSops(encrypted, wrongKey));
    }

    [Fact]
    public void SopsMode_ShouldFailOnTamperedMAC()
    {
        var keyPair = EciesModule.GenerateKeyPair("test");
        var secrets = new Dictionary<string, string> { ["key"] = "value" };

        var encrypted = GenerateSops(secrets, keyPair, "app.test.00001");

        // Tamper with a value
        encrypted.Values["key"] = "ENC[AesGcm:AAAA" + encrypted.Values["key"].Substring("ENC[AesGcm:AAAA".Length);

        Assert.ThrowsAny<Exception>(() => DecodeSops(encrypted, keyPair));
    }

    [Fact]
    public void WholeFileMode_RoundTrip_ShouldDecryptToOriginalJson()
    {
        var keyPair = EciesModule.GenerateKeyPair("test");
        var originalJson = "{\"db\":\"server=prod\",\"key\":\"abc123\"}";

        var encrypted = GenerateWholeFile(originalJson, keyPair, "app.test.00001");
        var decrypted = DecodeWholeFile(encrypted, keyPair);

        var original = JsonSerializer.Deserialize<JsonElement>(originalJson);
        var result = JsonSerializer.Deserialize<JsonElement>(decrypted);

        Assert.Equal(
            JsonSerializer.Serialize(original),
            JsonSerializer.Serialize(result));
    }

    [Fact]
    public void WholeFileMode_ShouldFailWithWrongKey()
    {
        var keyPair = EciesModule.GenerateKeyPair("right");
        var wrongKey = EciesModule.GenerateKeyPair("wrong");
        var originalJson = "{\"secret\":\"value\"}";

        var encrypted = GenerateWholeFile(originalJson, keyPair, "app.right.00001");

        Assert.ThrowsAny<Exception>(() => DecodeWholeFile(encrypted, wrongKey));
    }

    [Fact]
    public void UnflattenKeys_ShouldReconstructNestedStructure()
    {
        var keyPair = EciesModule.GenerateKeyPair("test");
        var secrets = new Dictionary<string, string>
        {
            ["Name"] = "myapp",
            ["Values:DbHost"] = "server.com",
            ["Values:Dropbox:AppKey"] = "abc",
            ["Values:Dropbox:AppSecret"] = "xyz",
            ["Items:0"] = "first",
            ["Items:1"] = "second"
        };

        var encrypted = GenerateSops(secrets, keyPair, "app.test.00001");
        var sopsJson = JsonSerializer.Serialize(encrypted, WriteOptions);

        // Decode and parse back
        var decryptedJson = DecodeSopsToJson(sopsJson, keyPair);
        var doc = JsonSerializer.Deserialize<JsonElement>(decryptedJson);

        Assert.Equal("myapp", doc.GetProperty("Name").GetString());
        Assert.Equal("server.com", doc.GetProperty("Values").GetProperty("DbHost").GetString());
        Assert.Equal("abc", doc.GetProperty("Values").GetProperty("Dropbox").GetProperty("AppKey").GetString());
        Assert.Equal("xyz", doc.GetProperty("Values").GetProperty("Dropbox").GetProperty("AppSecret").GetString());
        Assert.Equal("first", doc.GetProperty("Items")[0].GetString());
        Assert.Equal("second", doc.GetProperty("Items")[1].GetString());
    }

    [Fact]
    public void ParseTagFromVaultKey_ShouldExtractTag()
    {
        // vaultKey: "name.tag.version" → tag is second-to-last
        Assert.Equal("prod", ExtractTag("myapp.prod.00001"));
        Assert.Equal("develop", ExtractTag("app.develop.00002"));
        Assert.Equal("staging", ExtractTag(".staging.00001"));
    }

    // --- Helper methods that mirror the actual encode/decode logic ---

    private static SopsEncryptedFile GenerateSops(
        Dictionary<string, string> secrets, KeyPairModel keyPair, string vaultKey)
    {
        var dataKey = AesGcmHelper.GenerateDataKey();
        var encryptedValues = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in secrets)
            encryptedValues[kvp.Key] = AesGcmHelper.EncryptValue(dataKey, kvp.Value);

        var mac = AesGcmHelper.ComputeMac(dataKey, encryptedValues);
        var macEncrypted = AesGcmHelper.EncryptValue(dataKey, Convert.ToBase64String(mac));
        var wrappedDataKey = EciesModule.Seal(dataKey, keyPair.GetPublicKeyBytes());

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(dataKey);

        return new SopsEncryptedFile
        {
            Values = new Dictionary<string, string>(encryptedValues),
            Metadata = new SopsMetadata
            {
                VaultKey = vaultKey,
                Mode = "sops",
                DataKey = Convert.ToBase64String(wrappedDataKey),
                Mac = macEncrypted
            }
        };
    }

    private static Dictionary<string, string> DecodeSops(SopsEncryptedFile sopsFile, KeyPairModel keyPair)
    {
        var wrappedDataKey = Convert.FromBase64String(sopsFile.Metadata.DataKey);
        var dataKey = EciesModule.Open(wrappedDataKey, keyPair.GetPrivateKeyBytes());

        var macBase64 = AesGcmHelper.DecryptValue(dataKey, sopsFile.Metadata.Mac);
        var expectedMac = Convert.FromBase64String(macBase64);
        var sortedValues = new SortedDictionary<string, string>(sopsFile.Values, StringComparer.Ordinal);

        if (!AesGcmHelper.VerifyMac(dataKey, sortedValues, expectedMac))
            throw new InvalidOperationException("MAC verification failed");

        var result = new Dictionary<string, string>();
        foreach (var kvp in sopsFile.Values)
            result[kvp.Key] = AesGcmHelper.DecryptValue(dataKey, kvp.Value);

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(dataKey);
        return result;
    }

    private static string DecodeSopsToJson(string sopsJson, KeyPairModel keyPair)
    {
        var sopsFile = JsonSerializer.Deserialize<SopsEncryptedFile>(sopsJson)!;
        var wrappedDataKey = Convert.FromBase64String(sopsFile.Metadata.DataKey);
        var dataKey = EciesModule.Open(wrappedDataKey, keyPair.GetPrivateKeyBytes());

        var macBase64 = AesGcmHelper.DecryptValue(dataKey, sopsFile.Metadata.Mac);
        var expectedMac = Convert.FromBase64String(macBase64);
        var sortedValues = new SortedDictionary<string, string>(sopsFile.Values, StringComparer.Ordinal);
        if (!AesGcmHelper.VerifyMac(dataKey, sortedValues, expectedMac))
            throw new InvalidOperationException("MAC verification failed");

        var decrypted = new Dictionary<string, string>();
        foreach (var kvp in sopsFile.Values)
            decrypted[kvp.Key] = AesGcmHelper.DecryptValue(dataKey, kvp.Value);

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(dataKey);

        // Use the same unflatten logic as DecodeCommands
        var root = new Dictionary<string, object>();
        foreach (var kvp in decrypted)
        {
            var segments = kvp.Key.Split(':');
            SetNested(root, segments, 0, kvp.Value);
        }

        return JsonSerializer.Serialize(root, WriteOptions);
    }

    private static StashlockEncryptedFile GenerateWholeFile(
        string secretsJson, KeyPairModel keyPair, string vaultKey)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(secretsJson);
        var sealedBytes = EciesModule.Seal(plaintextBytes, keyPair.GetPublicKeyBytes());
        return new StashlockEncryptedFile
        {
            VaultKey = vaultKey,
            EncryptedData = Convert.ToBase64String(sealedBytes)
        };
    }

    private static string DecodeWholeFile(StashlockEncryptedFile encFile, KeyPairModel keyPair)
    {
        var sealedBytes = Convert.FromBase64String(encFile.EncryptedData);
        var plaintext = EciesModule.Open(sealedBytes, keyPair.GetPrivateKeyBytes());
        return Encoding.UTF8.GetString(plaintext);
    }

    private static string? ExtractTag(string vaultKey)
    {
        var parts = vaultKey.Split('.');
        return parts.Length >= 3 ? parts[^2] : null;
    }

    private static void SetNested(Dictionary<string, object> current, string[] segments, int index, string value)
    {
        var segment = segments[index];
        if (index == segments.Length - 1)
        {
            current[segment] = value;
            return;
        }

        var nextIsArray = int.TryParse(segments[index + 1], out _);
        if (!current.TryGetValue(segment, out var existing))
        {
            if (nextIsArray)
            {
                var list = new List<object>();
                current[segment] = list;
                SetNestedList(list, segments, index + 1, value);
            }
            else
            {
                var dict = new Dictionary<string, object>();
                current[segment] = dict;
                SetNested(dict, segments, index + 1, value);
            }
        }
        else if (existing is Dictionary<string, object> d)
            SetNested(d, segments, index + 1, value);
        else if (existing is List<object> l)
            SetNestedList(l, segments, index + 1, value);
    }

    private static void SetNestedList(List<object> list, string[] segments, int index, string value)
    {
        if (!int.TryParse(segments[index], out var i)) return;
        while (list.Count <= i) list.Add(null!);

        if (index == segments.Length - 1)
        {
            list[i] = value;
            return;
        }

        var nextIsArray = int.TryParse(segments[index + 1], out _);
        if (list[i] == null!)
        {
            if (nextIsArray)
            {
                var inner = new List<object>();
                list[i] = inner;
                SetNestedList(inner, segments, index + 1, value);
            }
            else
            {
                var dict = new Dictionary<string, object>();
                list[i] = dict;
                SetNested(dict, segments, index + 1, value);
            }
        }
        else if (list[i] is Dictionary<string, object> d)
            SetNested(d, segments, index + 1, value);
        else if (list[i] is List<object> l)
            SetNestedList(l, segments, index + 1, value);
    }
}
