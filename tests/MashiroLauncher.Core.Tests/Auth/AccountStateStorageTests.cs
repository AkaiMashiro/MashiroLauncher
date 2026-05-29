using MashiroLauncher.Core.Auth;

namespace MashiroLauncher.Core.Tests.Auth;

/// <summary>
/// AccountStateStorage just holds a pointer (active-account UUID). Tests verify
/// canonical hex normalization, atomic-ish writes, and graceful handling of a
/// missing or corrupt file.
/// </summary>
public class AccountStateStorageTests
{
    private static string FreshTempPath() =>
        Path.Combine(Path.GetTempPath(), $"mashiro-state-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Get_ReturnsNull_WhenFileMissing()
    {
        var storage = new AccountStateStorage(FreshTempPath());
        Assert.Null(await storage.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task SetThenGet_RoundTrips()
    {
        var path = FreshTempPath();
        try
        {
            var storage = new AccountStateStorage(path);
            var uuid = Guid.NewGuid().ToString("N");
            await storage.SetActiveAccountIdAsync(uuid);

            var loaded = await storage.GetActiveAccountIdAsync();
            Assert.Equal(uuid, loaded);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Set_NormalizesToCanonicalLowercaseHex()
    {
        var path = FreshTempPath();
        try
        {
            var storage = new AccountStateStorage(path);
            // Dashed + uppercase form should land as 32-hex lowercase.
            await storage.SetActiveAccountIdAsync("11111111-2222-3333-4444-555555555555");

            var loaded = await storage.GetActiveAccountIdAsync();
            Assert.Equal("11111111222233334444555555555555", loaded);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Set_Null_ClearsActiveAccount()
    {
        var path = FreshTempPath();
        try
        {
            var storage = new AccountStateStorage(path);
            await storage.SetActiveAccountIdAsync(Guid.NewGuid().ToString("N"));
            await storage.SetActiveAccountIdAsync(null);

            Assert.Null(await storage.GetActiveAccountIdAsync());
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Get_ReturnsNull_OnCorruptJson()
    {
        var path = FreshTempPath();
        try
        {
            await File.WriteAllTextAsync(path, "{ this is not valid json");

            var storage = new AccountStateStorage(path);
            Assert.Null(await storage.GetActiveAccountIdAsync());
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Get_ReturnsNull_WhenStoredValueIsNotAUuid()
    {
        var path = FreshTempPath();
        try
        {
            // Schema is technically valid JSON but the value isn't a parseable UUID.
            await File.WriteAllTextAsync(path, "{\"activeAccountId\":\"not-a-uuid\"}");

            var storage = new AccountStateStorage(path);
            Assert.Null(await storage.GetActiveAccountIdAsync());
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Delete_RemovesFile()
    {
        var path = FreshTempPath();
        try
        {
            var storage = new AccountStateStorage(path);
            await storage.SetActiveAccountIdAsync(Guid.NewGuid().ToString("N"));
            Assert.True(File.Exists(path));

            storage.Delete();
            Assert.False(File.Exists(path));
        }
        finally { TryDelete(path); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
