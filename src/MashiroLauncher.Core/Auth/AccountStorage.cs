using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MashiroLauncher.Core.Common;

namespace MashiroLauncher.Core.Auth;

public sealed record StoredAccount(
    string Username,
    string UuidHex,
    string MinecraftAccessToken,
    DateTimeOffset McTokenExpiresAt,
    string MsRefreshToken,
    string Xuid,
    string? SkinUrl)
{
    public MicrosoftAccount ToAccount()
    {
        var uuid = Guid.ParseExact(UuidHex.Replace("-", ""), "N");
        return new MicrosoftAccount(
            Username, uuid, MinecraftAccessToken, McTokenExpiresAt,
            MsRefreshToken, Xuid, SkinUrl);
    }

    public static StoredAccount FromAccount(MicrosoftAccount a) =>
        new(a.Username, a.Uuid.ToString("N"), a.AccessToken, a.McTokenExpiresAt,
            a.RefreshToken, a.Xuid, a.SkinUrl);
}

/// <summary>
/// Persists the signed-in Microsoft account to <c>data/accounts/microsoft.json</c>.
///
/// On Windows the file is wrapped in an envelope whose payload is encrypted
/// with DPAPI (CurrentUser scope), so a copy of the data directory is useless
/// on another Windows user account or another machine. On non-Windows hosts
/// we fall back to plaintext (the launcher itself is Windows-first; this path
/// only matters for tests or CLI-style usage of MashiroLauncher.Core).
///
/// A pre-DPAPI plaintext file is detected on load and silently migrated to
/// the encrypted envelope so the next load only takes the fast path.
/// </summary>
public sealed class AccountStorage
{
    private readonly string _filePath;

    public AccountStorage() : this(Path.Combine(Paths.Data, "accounts", "microsoft.json")) { }

    /// <summary>Override the on-disk path. Used by tests to avoid touching the real data dir.</summary>
    internal AccountStorage(string filePath) => _filePath = filePath;

    // Envelope schema version. v2 is the first encrypted format; we treat any
    // file without this key as legacy plaintext.
    private const int CurrentSchemaVersion = 2;

    public async Task SaveAsync(MicrosoftAccount account, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var dto = StoredAccount.FromAccount(account);
        var payloadJson = JsonSerializer.Serialize(dto, JsonOptions.Default);

        string fileContent;
        if (OperatingSystem.IsWindows())
        {
            var protectedBytes = ProtectWindows(Encoding.UTF8.GetBytes(payloadJson));
            var envelope = new StoredAccountEnvelope(
                CurrentSchemaVersion,
                WindowsDpapi: true,
                Payload: Convert.ToBase64String(protectedBytes));
            fileContent = JsonSerializer.Serialize(envelope, JsonOptions.Default);
        }
        else
        {
            // No good at-rest crypto on Linux/macOS without an external secret
            // store; keep plaintext but mark the schema so we don't try to
            // decrypt nothing on next load.
            var envelope = new StoredAccountEnvelope(
                CurrentSchemaVersion,
                WindowsDpapi: false,
                Payload: payloadJson);
            fileContent = JsonSerializer.Serialize(envelope, JsonOptions.Default);
        }

        await File.WriteAllTextAsync(_filePath, fileContent, ct);
    }

    public async Task<StoredAccount?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return null;

        string text;
        try { text = await File.ReadAllTextAsync(_filePath, ct); }
        catch { return null; }

        // Try the modern envelope first. Unknown / missing fields parse as
        // defaults, so a plaintext StoredAccount JSON deserializes here with
        // SchemaVersion = 0 and we fall through to the legacy path below.
        StoredAccountEnvelope? envelope = null;
        try { envelope = JsonSerializer.Deserialize<StoredAccountEnvelope>(text, JsonOptions.Default); }
        catch (JsonException) { /* not JSON at all — fall through */ }

        if (envelope is { SchemaVersion: >= CurrentSchemaVersion, Payload: { Length: > 0 } payload })
        {
            try
            {
                string innerJson;
                if (envelope.WindowsDpapi)
                {
                    if (!OperatingSystem.IsWindows()) return null;  // can't decrypt off-Windows
                    var protectedBytes = Convert.FromBase64String(payload);
                    var clearBytes = UnprotectWindows(protectedBytes);
                    innerJson = Encoding.UTF8.GetString(clearBytes);
                }
                else
                {
                    innerJson = payload;
                }
                return JsonSerializer.Deserialize<StoredAccount>(innerJson, JsonOptions.Default);
            }
            catch
            {
                // Corrupt envelope or DPAPI failure (e.g. user profile moved).
                // Signal "not signed in" instead of crashing.
                return null;
            }
        }

        // Legacy plaintext format. Parse it, then immediately re-save in the
        // encrypted envelope so subsequent loads take the fast path.
        StoredAccount? legacy = null;
        try { legacy = JsonSerializer.Deserialize<StoredAccount>(text, JsonOptions.Default); }
        catch { return null; }
        if (legacy is null) return null;

        try
        {
            // Migration is best-effort. If it fails we still return the loaded
            // account so the user stays signed in this session.
            await SaveAsync(legacy.ToAccount(), ct);
        }
        catch { /* swallow */ }

        return legacy;
    }

    public void Delete()
    {
        if (File.Exists(_filePath)) File.Delete(_filePath);
    }

    // ---- DPAPI helpers (Windows-only) ---------------------------------------

    // ProtectedData is a Windows-only API; the OperatingSystem.IsWindows()
    // guards above keep us off this path elsewhere, but we still mark the
    // methods so the analyzer doesn't warn.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] data) =>
        ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] data) =>
        ProtectedData.Unprotect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);

    // ---- File-on-disk envelope ----------------------------------------------

    private sealed record StoredAccountEnvelope(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("windowsDpapi")] bool WindowsDpapi,
        [property: JsonPropertyName("payload")] string Payload);
}
