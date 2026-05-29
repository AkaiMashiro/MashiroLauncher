using System.Text.Json;
using System.Text.Json.Serialization;
using MashiroLauncher.Core.Common;

namespace MashiroLauncher.Core.Auth;

/// <summary>
/// Tracks which of the stored Microsoft accounts is currently "active" — i.e.
/// the one Play uses when an instance leaves account selection on Default and
/// the global offline-mode toggle is off. Lives at
/// <c>data/accounts/state.json</c> as plaintext JSON; this is just a pointer
/// (a UUID hex string), no secrets, so DPAPI is unnecessary.
/// </summary>
public sealed class AccountStateStorage
{
    private readonly string _filePath;

    public AccountStateStorage() : this(Path.Combine(Paths.Data, "accounts", "state.json")) { }

    /// <summary>Override the on-disk path. Used by tests.</summary>
    internal AccountStateStorage(string filePath) => _filePath = filePath;

    /// <summary>Currently-active Microsoft account UUID (32-hex form), or null if none/offline-only.</summary>
    public async Task<string?> GetActiveAccountIdAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(_filePath, ct);
            var state = JsonSerializer.Deserialize<AccountStateDto>(text, JsonOptions.Default);
            return Normalize(state?.ActiveAccountId);
        }
        catch
        {
            // Malformed state file — treat as "no active account" rather than
            // crashing the app on startup.
            return null;
        }
    }

    /// <summary>
    /// Sets the active account UUID. Pass null to clear (offline-only). The
    /// file is written atomically enough for our purposes (single short write)
    /// — we don't bother with temp-file rename dance.
    /// </summary>
    public async Task SetActiveAccountIdAsync(string? uuidHex, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var normalized = Normalize(uuidHex);
        var dto = new AccountStateDto(normalized);
        var json = JsonSerializer.Serialize(dto, JsonOptions.Default);
        await File.WriteAllTextAsync(_filePath, json, ct);
    }

    public void Delete()
    {
        if (File.Exists(_filePath)) File.Delete(_filePath);
    }

    /// <summary>
    /// Canonicalize the UUID to 32-hex lowercase so different write paths
    /// (dashed Guid, mixed case, …) compare cleanly with file names.
    /// </summary>
    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Guid.TryParse(raw, out var g) ? g.ToString("N").ToLowerInvariant() : null;
    }

    private sealed record AccountStateDto(
        [property: JsonPropertyName("activeAccountId")] string? ActiveAccountId);
}
