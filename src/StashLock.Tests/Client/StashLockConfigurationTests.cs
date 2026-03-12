using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Deneblab.StashLock.Cli.Modules.Ecies;
using Deneblab.StashLock.Client.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;
using CliAesGcmHelper = Deneblab.StashLock.Cli.Modules.Ecies.AesGcmHelper;
using CliSopsEncryptedFile = Deneblab.StashLock.Cli.Modules.Encode.SopsEncryptedFile;
using CliSopsMetadata = Deneblab.StashLock.Cli.Modules.Encode.SopsMetadata;

namespace Deneblab.StashLock.Tests.Client;

public class StashLockConfigurationTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    private string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"stashlock-test-{Guid.NewGuid()}.json");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void AddStashLockDevFile_ShouldPopulateConfiguration()
    {
        // Arrange
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["db_host"] = "localhost",
            ["db_port"] = "5432",
            ["api_key"] = "dev-key-123"
        });
        var filePath = WriteTempFile(json);

        // Act
        var config = new ConfigurationBuilder()
            .AddStashLock(cfg => cfg.FromDevFile(filePath))
            .Build();

        // Assert
        Assert.Equal("localhost", config["db_host"]);
        Assert.Equal("5432", config["db_port"]);
        Assert.Equal("dev-key-123", config["api_key"]);
    }

    [Fact]
    public void AddStashLockDevFile_ShouldFlattenNestedJson()
    {
        // Arrange — nested JSON object
        var json = JsonSerializer.Serialize(new
        {
            Database = new { Host = "prod-server", Port = 5432, Password = "secret" },
            Api = new { Key = "abc123" }
        });
        var filePath = WriteTempFile(json);

        // Act
        var config = new ConfigurationBuilder()
            .AddStashLock(cfg => cfg.FromDevFile(filePath))
            .Build();

        // Assert — colon-separated keys
        Assert.Equal("prod-server", config["Database:Host"]);
        Assert.Equal("5432", config["Database:Port"]);
        Assert.Equal("secret", config["Database:Password"]);
        Assert.Equal("abc123", config["Api:Key"]);
    }

    [Fact]
    public void AddStashLockDevFile_ShouldSupportGetSection()
    {
        // Arrange
        var json = JsonSerializer.Serialize(new
        {
            Database = new { Host = "server1", Port = 3306 },
            Redis = new { Host = "redis1", Port = 6379 }
        });
        var filePath = WriteTempFile(json);

        // Act
        var config = new ConfigurationBuilder()
            .AddStashLock(cfg => cfg.FromDevFile(filePath))
            .Build();

        var dbSection = config.GetSection("Database");
        var redisSection = config.GetSection("Redis");

        // Assert
        Assert.Equal("server1", dbSection["Host"]);
        Assert.Equal("3306", dbSection["Port"]);
        Assert.Equal("redis1", redisSection["Host"]);
        Assert.Equal("6379", redisSection["Port"]);
    }

    [Fact]
    public void AddStashLockDevFile_ShouldSupportCommentsAndTrailingCommas()
    {
        // Arrange — JSON with comments and trailing commas
        var json = """
        {
            // Database settings
            "db_host": "localhost",
            "db_port": "5432",
        }
        """;
        var filePath = WriteTempFile(json);

        // Act
        var config = new ConfigurationBuilder()
            .AddStashLock(cfg => cfg.FromDevFile(filePath))
            .Build();

        // Assert
        Assert.Equal("localhost", config["db_host"]);
        Assert.Equal("5432", config["db_port"]);
    }

    [Fact]
    public void AddStashLockDevFile_ShouldBeCaseInsensitive()
    {
        // Arrange
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["DB_HOST"] = "server1"
        });
        var filePath = WriteTempFile(json);

        // Act
        var config = new ConfigurationBuilder()
            .AddStashLock(cfg => cfg.FromDevFile(filePath))
            .Build();

        // Assert — IConfiguration is case-insensitive
        Assert.Equal("server1", config["db_host"]);
        Assert.Equal("server1", config["DB_HOST"]);
        Assert.Equal("server1", config["Db_Host"]);
    }

    [Fact]
    public void AddStashLockDevFile_ShouldThrowForMissingFile()
    {
        // Act & Assert
        Assert.Throws<FileNotFoundException>(() =>
        {
            new ConfigurationBuilder()
                .AddStashLock(cfg => cfg.FromDevFile("/nonexistent/path/secrets.json"))
                .Build();
        });
    }

    [Fact]
    public void AddStashLockFile_ShouldDecryptSopsEncryptedFile()
    {
        // Arrange — create SOPS-encrypted file using CLI code
        var keyPair = EciesModule.GenerateKeyPair("config-test");
        var secrets = new Dictionary<string, string>
        {
            ["Database:Host"] = "prod-db.example.com",
            ["Database:Port"] = "5432",
            ["Api:Key"] = "sk-prod-abc123"
        };

        var sopsFile = GenerateSopsFile(secrets, keyPair, "app.prod.00001");
        var sopsJson = JsonSerializer.Serialize(sopsFile, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var filePath = WriteTempFile(sopsJson);
        var privateKeyBase64 = Convert.ToBase64String(keyPair.GetPrivateKeyBytes());

        // Act
        var config = new ConfigurationBuilder()
            .AddStashLock(cfg => cfg.FromEncryptedFile(filePath).WithPrivateKey(privateKeyBase64))
            .Build();

        // Assert
        Assert.Equal("prod-db.example.com", config["Database:Host"]);
        Assert.Equal("5432", config["Database:Port"]);
        Assert.Equal("sk-prod-abc123", config["Api:Key"]);

        // Also test GetSection
        var dbSection = config.GetSection("Database");
        Assert.Equal("prod-db.example.com", dbSection["Host"]);
    }

    [Fact]
    public void AddStashLockFile_ShouldDecryptWholeFileMode()
    {
        // Arrange — create whole-file encrypted file using CLI code
        var keyPair = EciesModule.GenerateKeyPair("whole-test");
        var plainJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["secret_key"] = "value123",
            ["another_key"] = "value456"
        });

        var sealedBytes = EciesModule.Seal(
            Encoding.UTF8.GetBytes(plainJson),
            keyPair.GetPublicKeyBytes());

        var encFile = new Deneblab.StashLock.Cli.Modules.Encode.StashlockEncryptedFile
        {
            VaultKey = "app.test.00001",
            EncryptedData = Convert.ToBase64String(sealedBytes)
        };
        var encJson = JsonSerializer.Serialize(encFile, new JsonSerializerOptions { WriteIndented = true });
        var filePath = WriteTempFile(encJson);
        var privateKeyBase64 = Convert.ToBase64String(keyPair.GetPrivateKeyBytes());

        // Act
        var config = new ConfigurationBuilder()
            .AddStashLock(cfg => cfg.FromEncryptedFile(filePath).WithPrivateKey(privateKeyBase64))
            .Build();

        // Assert
        Assert.Equal("value123", config["secret_key"]);
        Assert.Equal("value456", config["another_key"]);
    }

    [Fact]
    public void AddStashLockFile_ShouldChainWithOtherSources()
    {
        // Arrange — base config + StashLock override
        var baseJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["db_host"] = "default-host",
            ["db_port"] = "3306"
        });
        var baseFile = WriteTempFile(baseJson);

        var overrideJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["db_host"] = "prod-host"
        });
        var overrideFile = WriteTempFile(overrideJson);

        // Act — StashLock source overrides base
        var config = new ConfigurationBuilder()
            .AddStashLock(cfg => cfg.FromDevFile(baseFile))
            .AddStashLock(cfg => cfg.FromDevFile(overrideFile))
            .Build();

        // Assert — last source wins
        Assert.Equal("prod-host", config["db_host"]);
        Assert.Equal("3306", config["db_port"]);
    }

    // --- Helper: mirrors CLI's SOPS encode logic ---
    private static CliSopsEncryptedFile GenerateSopsFile(
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
