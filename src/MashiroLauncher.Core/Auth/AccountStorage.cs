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
/// Persists Microsoft accounts under <c>data/accounts/{uuidHex}.json</c>.
///
/// On Windows each file is wrapped in a DPAPI (CurrentUser scope) envelope so a
/// copy of the data directory is useless on another Windows user account or
/// another machine. On non-Windows hosts we fall back to plaintext (the launcher
/// itself is Windows-first; this path only matters for tests or CLI-style usage
/// of MashiroLauncher.Core).
///
/// Storage layout history:
/// <list type="bullet">
///   <item><description>v1: <c>accounts/microsoft.json</c> — bare plaintext <see cref="StoredAccount"/> JSON.</description></item>
///   <item><description>v2: <c>accounts/microsoft.json</c> — DPAPI envelope (single account).</description></item>
///   <item><description>v3: <c>accounts/{uuidHex}.json</c> — DPAPI envelope per account (current).</description></item>
/// </list>
///
/// <see cref="LoadAllAsync"/> migrates v1/v2 (single-file) on first load:
/// decrypts the legacy file, re-saves it as <c>{uuidHex}.json</c> (v3), then
/// deletes the legacy. Subsequent loads skip the migration step.
/// </summary>
public sealed class AccountStorage
{
    private readonly string _accountsDir;
    private readonly string _legacyFilePath;

    public AccountStorage() : this(Path.Combine(Paths.Data, "accounts")) { }

    /// <summary>Override the on-disk directory. Used by tests to avoid touching the real data dir.</summary>
    internal AccountStorage(string accountsDir)
    {
        _accountsDir = accountsDir;
        _legacyFilePath = Path.Combine(accountsDir, "microsoft.json");
    }

    /// <summary>Envelope schema version written by <see cref="SaveAsync"/>.</summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>Reserved filenames inside accounts/ that don't represent accounts.</summary>
    private static readonly HashSet<string> NonAccountFiles =
        new(StringComparer.OrdinalIgnoreCase) { "microsoft.json", "state.json" };

    /// <summary>Files we'll accept as an account envelope (regardless of schema version).</summary>
    private const int MinAcceptedSchemaVersion = 2;

    // ---- Public API ---------------------------------------------------------

    /// <summary>
    /// Loads every stored account. Performs a one-shot migration of the v1/v2
    /// single-file format on first call. Returns an empty list if no accounts
    /// exist (or the directory is missing).
    /// </summary>
    public async Task<IReadOnlyList<StoredAccount>> LoadAllAsync(CancellationToken ct = default)
    {
        await MigrateLegacyIfPresentAsync(ct);

        if (!Directory.Exists(_accountsDir))
            return Array.Empty<StoredAccount>();

        var results = new List<StoredAccount>();
        foreach (var path in Directory.GetFiles(_accountsDir, "*.json"))
        {
            var name = Path.GetFileName(path);
            if (NonAccountFiles.Contains(name)) continue;

            var loaded = await TryLoadFileAsync(path, ct);
            if (loaded is not null) results.Add(loaded);
        }
        return results;
    }

    /// <summary>
    /// Writes the account to <c>accounts/{uuidHex}.json</c>, overwriting any
    /// existing entry for the same UUID. The file is DPAPI-encrypted on Windows.
    /// </summary>
    public async Task SaveAsync(MicrosoftAccount account, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_accountsDir);
        var dto = StoredAccount.FromAccount(account);
        var path = GetAccountPath(account.Uuid.ToString("N"));
        await WriteEnvelopeAsync(path, dto, ct);
    }

    /// <summary>
    /// Deletes the per-account file for the given UUID. Returns true if the
    /// account file existed at the start of the call (and is now gone), false
    /// if there was nothing to remove. The File.Exists + File.Delete pair has
    /// a benign TOCTOU window — if a concurrent caller deletes the file
    /// between our two checks, File.Delete is idempotent (no-op on missing)
    /// so we still return the semantically correct "yes, the account is
    /// gone." Permission / locked-file errors swallow to false.
    /// </summary>
    public bool Remove(string uuidHex)
    {
        var path = GetAccountPath(uuidHex);
        if (!File.Exists(path)) return false;
        try { File.Delete(path); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Deletes every per-account file. The state.json companion is left to
    /// <see cref="AccountStateStorage"/> to clean up.
    /// </summary>
    public void RemoveAll()
    {
        if (!Directory.Exists(_accountsDir)) return;
        foreach (var path in Directory.GetFiles(_accountsDir, "*.json"))
        {
            var name = Path.GetFileName(path);
            if (NonAccountFiles.Contains(name)) continue;
            try { File.Delete(path); }
            catch { /* best-effort: a locked file just stays around */ }
        }
        // Legacy single-file: also clear it if somehow still present.
        try { if (File.Exists(_legacyFilePath)) File.Delete(_legacyFilePath); }
        catch { /* best-effort */ }
    }

    // ---- File I/O internals -------------------------------------------------

    private string GetAccountPath(string uuidHex) =>
        Path.Combine(_accountsDir, $"{SanitizeUuid(uuidHex)}.json");

    /// <summary>
    /// Force the filename to a canonical 32-hex form so different callers
    /// (e.g. dashed Guid vs "N" format) always land on the same file.
    /// </summary>
    private static string SanitizeUuid(string uuidHex)
    {
        if (string.IsNullOrWhiteSpace(uuidHex))
            throw new ArgumentException("UUID is required.", nameof(uuidHex));
        if (Guid.TryParse(uuidHex, out var g))
            return g.ToString("N").ToLowerInvariant();
        throw new ArgumentException($"Not a valid UUID: {uuidHex}", nameof(uuidHex));
    }

    private async Task<StoredAccount?> TryLoadFileAsync(string path, CancellationToken ct)
    {
        string text;
        try { text = await File.ReadAllTextAsync(path, ct); }
        catch { return null; }

        // Try the modern envelope first. Unknown / missing fields parse as
        // defaults, so a plaintext StoredAccount JSON deserializes here with
        // SchemaVersion = 0 and falls through to the legacy path below.
        StoredAccountEnvelope? envelope = null;
        try { envelope = JsonSerializer.Deserialize<StoredAccountEnvelope>(text, JsonOptions.Default); }
        catch (JsonException) { /* not JSON at all — fall through */ }

        if (envelope is { SchemaVersion: >= MinAcceptedSchemaVersion, Payload: { Length: > 0 } payload })
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
                // Signal "not signed in" for this account instead of crashing
                // the whole load.
                return null;
            }
        }

        // Legacy plaintext format (v1). Parse it; the caller (MigrateLegacy)
        // is responsible for re-saving in the encrypted envelope.
        try { return JsonSerializer.Deserialize<StoredAccount>(text, JsonOptions.Default); }
        catch { return null; }
    }

    /// <summary>
    /// If the v1/v2 single-account file (<c>accounts/microsoft.json</c>) still
    /// exists, decrypt it, re-save as <c>{uuidHex}.json</c>, and delete the
    /// legacy file. Idempotent: subsequent calls do nothing when the legacy
    /// file is gone.
    /// </summary>
    private async Task MigrateLegacyIfPresentAsync(CancellationToken ct)
    {
        if (!File.Exists(_legacyFilePath)) return;

        var legacy = await TryLoadFileAsync(_legacyFilePath, ct);
        if (legacy is null)
        {
            // Couldn't decrypt (different machine? user profile moved?). Leave
            // the file alone so the user can debug; we'll prompt re-login.
            return;
        }

        try
        {
            var account = legacy.ToAccount();
            var perAccountPath = GetAccountPath(account.Uuid.ToString("N"));
            // Don't clobber if a per-account file already exists (paranoia —
            // shouldn't happen during a clean migration but cheap to check).
            if (!File.Exists(perAccountPath))
                await SaveAsync(account, ct);
        }
        catch { /* migration is best-effort — the legacy file stays as fallback */ return; }

        try { File.Delete(_legacyFilePath); }
        catch { /* legacy file delete failed — next load will retry */ }
    }

    private async Task WriteEnvelopeAsync(string path, StoredAccount dto, CancellationToken ct)
    {
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

        await File.WriteAllTextAsync(path, fileContent, ct);
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
