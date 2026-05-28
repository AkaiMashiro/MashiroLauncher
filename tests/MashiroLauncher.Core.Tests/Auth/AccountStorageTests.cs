using MashiroLauncher.Core.Auth;

namespace MashiroLauncher.Core.Tests.Auth;

/// <summary>
/// These tests exercise the DPAPI envelope round-trip and the legacy-plaintext
/// migration. DPAPI is Windows-only so the tests skip themselves on other OSes.
/// </summary>
public class AccountStorageTests
{
    private static string FreshTempPath() =>
        Path.Combine(Path.GetTempPath(), $"mashiro-acct-{Guid.NewGuid():N}.json");

    private static MicrosoftAccount SampleAccount(string username = "Tester") =>
        new(username, Guid.NewGuid(),
            mcAccessToken: "mc-access-xyz",
            mcTokenExpiresAt: DateTimeOffset.UtcNow.AddHours(1).TrimSubMs(),
            msRefreshToken: "refresh-abc",
            xuid: "1234567890",
            skinUrl: null);

    [Fact]
    public async Task SaveLoad_RoundTripsAllFields()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = FreshTempPath();
        try
        {
            var storage = new AccountStorage(path);
            var original = SampleAccount("Mashiro");
            await storage.SaveAsync(original);

            var loaded = await storage.LoadAsync();
            Assert.NotNull(loaded);
            Assert.Equal(original.Username, loaded!.Username);
            Assert.Equal(original.Uuid.ToString("N"), loaded.UuidHex);
            Assert.Equal(original.AccessToken, loaded.MinecraftAccessToken);
            Assert.Equal(original.RefreshToken, loaded.MsRefreshToken);
            Assert.Equal(original.Xuid, loaded.Xuid);
            Assert.Equal(original.McTokenExpiresAt, loaded.McTokenExpiresAt);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Save_ProducesEnvelope_NotPlaintext()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = FreshTempPath();
        try
        {
            var storage = new AccountStorage(path);
            await storage.SaveAsync(SampleAccount());

            var content = await File.ReadAllTextAsync(path);
            // Envelope keys are present.
            Assert.Contains("\"schemaVersion\"", content);
            Assert.Contains("\"windowsDpapi\":true", content);
            Assert.Contains("\"payload\"", content);
            // Sensitive values must NOT appear in cleartext.
            Assert.DoesNotContain("refresh-abc", content);
            Assert.DoesNotContain("mc-access-xyz", content);
            Assert.DoesNotContain("Tester", content);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Load_LegacyPlaintext_MigratesToEnvelope()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = FreshTempPath();
        try
        {
            // Pre-DPAPI shape: a bare StoredAccount JSON the way v1 wrote it.
            const string legacy = """
                {
                  "username":"OldUser",
                  "uuidHex":"00000000000000000000000000000001",
                  "minecraftAccessToken":"legacy-token",
                  "mcTokenExpiresAt":"2099-01-01T00:00:00+00:00",
                  "msRefreshToken":"legacy-refresh",
                  "xuid":"42",
                  "skinUrl":null
                }
                """;
            await File.WriteAllTextAsync(path, legacy);

            var storage = new AccountStorage(path);
            var loaded = await storage.LoadAsync();
            Assert.NotNull(loaded);
            Assert.Equal("OldUser", loaded!.Username);
            Assert.Equal("legacy-token", loaded.MinecraftAccessToken);
            Assert.Equal("legacy-refresh", loaded.MsRefreshToken);

            // File on disk should now be in the encrypted envelope form, with no
            // plaintext token left behind.
            var after = await File.ReadAllTextAsync(path);
            Assert.Contains("\"schemaVersion\":2", after);
            Assert.DoesNotContain("legacy-token", after);
            Assert.DoesNotContain("legacy-refresh", after);
            Assert.DoesNotContain("OldUser", after);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Load_ReturnsNull_WhenFileMissing()
    {
        var storage = new AccountStorage(FreshTempPath());  // never created
        Assert.Null(await storage.LoadAsync());
    }

    [Fact]
    public async Task Load_ReturnsNull_OnCorruptEnvelope()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = FreshTempPath();
        try
        {
            // Valid envelope shape but the base64 payload is garbage from a
            // different machine's DPAPI key.
            const string corrupt = """
                {"schemaVersion":2,"windowsDpapi":true,"payload":"AQIDBAUGBwgJCgsMDQ4PEA=="}
                """;
            await File.WriteAllTextAsync(path, corrupt);

            var storage = new AccountStorage(path);
            Assert.Null(await storage.LoadAsync());
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Delete_RemovesFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        var path = FreshTempPath();
        try
        {
            var storage = new AccountStorage(path);
            await storage.SaveAsync(SampleAccount());
            Assert.True(File.Exists(path));

            storage.Delete();
            Assert.False(File.Exists(path));
        }
        finally { TryDelete(path); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* test cleanup is best-effort */ }
    }
}

file static class TestExtensions
{
    /// <summary>
    /// JSON serializer round-trip rounds DateTimeOffset to millisecond precision;
    /// trim ours up front so equality comparisons match exactly.
    /// </summary>
    public static DateTimeOffset TrimSubMs(this DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMillisecond), value.Offset);
}
