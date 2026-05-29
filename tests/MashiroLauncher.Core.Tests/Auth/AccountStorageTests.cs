using MashiroLauncher.Core.Auth;

namespace MashiroLauncher.Core.Tests.Auth;

/// <summary>
/// Exercises the per-account DPAPI envelope round-trip, multi-account loading,
/// and the v1/v2 → v3 legacy migration. DPAPI is Windows-only so the encrypted
/// paths skip themselves on other OSes.
/// </summary>
public class AccountStorageTests
{
    private static string FreshTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mashiro-acct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static MicrosoftAccount SampleAccount(string username = "Tester", Guid? uuid = null) =>
        new(username, uuid ?? Guid.NewGuid(),
            mcAccessToken: "mc-access-xyz",
            mcTokenExpiresAt: DateTimeOffset.UtcNow.AddHours(1).TrimSubMs(),
            msRefreshToken: "refresh-abc",
            xuid: "1234567890",
            skinUrl: null);

    // ---- Round-trip ---------------------------------------------------------

    [Fact]
    public async Task SaveLoad_RoundTripsAllFields()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = FreshTempDir();
        try
        {
            var storage = new AccountStorage(dir);
            var original = SampleAccount("Mashiro");
            await storage.SaveAsync(original);

            var all = await storage.LoadAllAsync();
            var loaded = Assert.Single(all);
            Assert.Equal(original.Username, loaded.Username);
            Assert.Equal(original.Uuid.ToString("N"), loaded.UuidHex);
            Assert.Equal(original.AccessToken, loaded.MinecraftAccessToken);
            Assert.Equal(original.RefreshToken, loaded.MsRefreshToken);
            Assert.Equal(original.Xuid, loaded.Xuid);
            Assert.Equal(original.McTokenExpiresAt, loaded.McTokenExpiresAt);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Save_ProducesEnvelope_NotPlaintext()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = FreshTempDir();
        try
        {
            var storage = new AccountStorage(dir);
            var account = SampleAccount();
            await storage.SaveAsync(account);

            // The per-account file should exist with the UUID-hex stem.
            var expectedPath = Path.Combine(dir, $"{account.Uuid:N}.json");
            Assert.True(File.Exists(expectedPath), $"expected {expectedPath} on disk");

            var content = await File.ReadAllTextAsync(expectedPath);
            Assert.Contains("\"schemaVersion\"", content);
            Assert.Contains("\"windowsDpapi\":true", content);
            Assert.Contains("\"payload\"", content);
            // Sensitive values must NOT appear in cleartext.
            Assert.DoesNotContain("refresh-abc", content);
            Assert.DoesNotContain("mc-access-xyz", content);
            Assert.DoesNotContain("Tester", content);
        }
        finally { TryDelete(dir); }
    }

    // ---- Multi-account ------------------------------------------------------

    [Fact]
    public async Task LoadAll_ReturnsEverySavedAccount()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = FreshTempDir();
        try
        {
            var storage = new AccountStorage(dir);
            var a = SampleAccount("Alpha");
            var b = SampleAccount("Beta");
            var c = SampleAccount("Gamma");
            await storage.SaveAsync(a);
            await storage.SaveAsync(b);
            await storage.SaveAsync(c);

            var loaded = await storage.LoadAllAsync();
            Assert.Equal(3, loaded.Count);

            var names = loaded.Select(l => l.Username).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, names);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Save_TwiceForSameUuid_OverwritesPreviousFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = FreshTempDir();
        try
        {
            var storage = new AccountStorage(dir);
            var uuid = Guid.NewGuid();
            await storage.SaveAsync(SampleAccount("First", uuid));
            await storage.SaveAsync(SampleAccount("Second", uuid));

            var loaded = await storage.LoadAllAsync();
            var single = Assert.Single(loaded);
            Assert.Equal("Second", single.Username);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Remove_DeletesOnlyTheTargetAccount()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = FreshTempDir();
        try
        {
            var storage = new AccountStorage(dir);
            var keep = SampleAccount("Keeper");
            var drop = SampleAccount("Dropped");
            await storage.SaveAsync(keep);
            await storage.SaveAsync(drop);

            Assert.True(storage.Remove(drop.Uuid.ToString("N")));

            var remaining = await storage.LoadAllAsync();
            var single = Assert.Single(remaining);
            Assert.Equal("Keeper", single.Username);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Remove_ReturnsFalse_WhenAccountMissing()
    {
        var dir = FreshTempDir();
        try
        {
            var storage = new AccountStorage(dir);
            // No files at all in dir.
            Assert.False(storage.Remove(Guid.NewGuid().ToString("N")));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task RemoveAll_ClearsEveryAccount_ButLeavesStateJson()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = FreshTempDir();
        try
        {
            var storage = new AccountStorage(dir);
            await storage.SaveAsync(SampleAccount("A"));
            await storage.SaveAsync(SampleAccount("B"));

            // state.json is owned by AccountStateStorage; emulate one being present.
            var statePath = Path.Combine(dir, "state.json");
            await File.WriteAllTextAsync(statePath, "{\"activeAccountId\":\"deadbeef\"}");

            storage.RemoveAll();

            var loaded = await storage.LoadAllAsync();
            Assert.Empty(loaded);
            Assert.True(File.Exists(statePath), "state.json should survive RemoveAll");
        }
        finally { TryDelete(dir); }
    }

    // ---- Legacy migration ---------------------------------------------------

    [Fact]
    public async Task Load_LegacyV1Plaintext_MigratesToPerAccountFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = FreshTempDir();
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
            var legacyPath = Path.Combine(dir, "microsoft.json");
            await File.WriteAllTextAsync(legacyPath, legacy);

            var storage = new AccountStorage(dir);
            var loaded = await storage.LoadAllAsync();
            var single = Assert.Single(loaded);
            Assert.Equal("OldUser", single.Username);
            Assert.Equal("legacy-token", single.MinecraftAccessToken);
            Assert.Equal("legacy-refresh", single.MsRefreshToken);

            // Legacy file should be gone; per-account file should exist.
            Assert.False(File.Exists(legacyPath));
            var perAccountPath = Path.Combine(dir, "00000000000000000000000000000001.json");
            Assert.True(File.Exists(perAccountPath));

            // Per-account file is in DPAPI envelope form — no plaintext secrets.
            var after = await File.ReadAllTextAsync(perAccountPath);
            Assert.Contains("\"schemaVersion\":3", after);
            Assert.DoesNotContain("legacy-token", after);
            Assert.DoesNotContain("legacy-refresh", after);
            Assert.DoesNotContain("OldUser", after);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Load_LegacyV2Envelope_MigratesToPerAccountFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = FreshTempDir();
        try
        {
            // Create a v2-style microsoft.json by saving via the same storage,
            // then renaming the resulting file to microsoft.json (since the
            // single-file path is the legacy convention).
            var storage = new AccountStorage(dir);
            var account = SampleAccount("V2User", Guid.Parse("11111111-1111-1111-1111-111111111111"));
            await storage.SaveAsync(account);

            var perAccountPath = Path.Combine(dir, $"{account.Uuid:N}.json");
            var legacyPath = Path.Combine(dir, "microsoft.json");
            File.Move(perAccountPath, legacyPath);

            // Now LoadAll should migrate the legacy file back to a per-account file.
            var loaded = await storage.LoadAllAsync();
            var single = Assert.Single(loaded);
            Assert.Equal("V2User", single.Username);
            Assert.False(File.Exists(legacyPath));
            Assert.True(File.Exists(perAccountPath));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task Load_LegacyV2_DoesNotClobber_ExistingPerAccountFile()
    {
        // Defense in depth: if somehow both forms exist, the per-account file wins.
        if (!OperatingSystem.IsWindows()) return;

        var dir = FreshTempDir();
        try
        {
            var storage = new AccountStorage(dir);
            var uuid = Guid.Parse("22222222-2222-2222-2222-222222222222");

            // First: write per-account file with "PerAccount" username
            await storage.SaveAsync(SampleAccount("PerAccount", uuid));

            // Then: also write a legacy microsoft.json with "Legacy" username, same UUID
            // (re-using the per-account file then moving it gets us a different payload).
            await storage.SaveAsync(SampleAccount("Legacy", uuid));
            var perAccountPath = Path.Combine(dir, $"{uuid:N}.json");
            var legacyPath = Path.Combine(dir, "microsoft.json");
            File.Copy(perAccountPath, legacyPath);
            // Re-save the "PerAccount" username back into the per-account file.
            await storage.SaveAsync(SampleAccount("PerAccount", uuid));

            var loaded = await storage.LoadAllAsync();
            // Migration must NOT overwrite the existing per-account file.
            var single = Assert.Single(loaded);
            Assert.Equal("PerAccount", single.Username);
            Assert.False(File.Exists(legacyPath));
        }
        finally { TryDelete(dir); }
    }

    // ---- Edge cases ---------------------------------------------------------

    [Fact]
    public async Task LoadAll_ReturnsEmpty_WhenDirectoryMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mashiro-acct-missing-{Guid.NewGuid():N}");
        // dir intentionally not created.
        var storage = new AccountStorage(dir);
        var loaded = await storage.LoadAllAsync();
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task LoadAll_SkipsStateJson_AndCorruptFiles()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = FreshTempDir();
        try
        {
            var storage = new AccountStorage(dir);
            await storage.SaveAsync(SampleAccount("Good"));

            // Decoy files that LoadAll should ignore / skip.
            await File.WriteAllTextAsync(Path.Combine(dir, "state.json"), "{\"activeAccountId\":\"abc\"}");
            await File.WriteAllTextAsync(Path.Combine(dir, "00000000000000000000000000000099.json"),
                "{\"schemaVersion\":2,\"windowsDpapi\":true,\"payload\":\"AQIDBAUGBwgJCgsMDQ4PEA==\"}");
            await File.WriteAllTextAsync(Path.Combine(dir, "notjson.json"), "this isn't json at all");

            var loaded = await storage.LoadAllAsync();
            // Only the good account should survive.
            var single = Assert.Single(loaded);
            Assert.Equal("Good", single.Username);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task LoadAll_ReturnsEmpty_WhenOnlyCorruptFilesPresent()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = FreshTempDir();
        try
        {
            // Valid envelope shape but the base64 payload is garbage from a
            // different machine's DPAPI key.
            const string corrupt = """
                {"schemaVersion":2,"windowsDpapi":true,"payload":"AQIDBAUGBwgJCgsMDQ4PEA=="}
                """;
            await File.WriteAllTextAsync(Path.Combine(dir, "deadbeefdeadbeefdeadbeefdeadbeef.json"), corrupt);

            var storage = new AccountStorage(dir);
            Assert.Empty(await storage.LoadAllAsync());
        }
        finally { TryDelete(dir); }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
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
