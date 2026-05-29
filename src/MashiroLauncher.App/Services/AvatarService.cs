using Avalonia.Media.Imaging;
using MashiroLauncher.Core.Common;

namespace MashiroLauncher.App.Services;

/// <summary>
/// Fetches the 24×24 Minecraft skin-head avatar for a given account from
/// <a href="https://minotar.net">Minotar</a>.
///
/// Online (Microsoft) accounts: <c>https://minotar.net/helm/{uuid}/24.png</c> —
/// the helm overlay variant so the cap layer reads as part of the head. The
/// PNG is cached on disk at <c>data/ui/avatars/{uuid}.png</c> so subsequent
/// boots are offline-friendly, and held in a per-process Bitmap cache so the
/// same UUID resolves instantly after the first request.
///
/// Offline accounts (no UUID): <see cref="GetSteveHeadAsync"/> returns
/// Minotar's <c>MHF_Steve</c> head — the Mojang-canonical default Steve face.
/// The Steve bitmap shares the same on-disk + memory cache as real heads
/// (keyed under "MHF_Steve") so the second offline card paints instantly.
///
/// Every call is best-effort: HTTP failure / decode failure / blank cache
/// surface as <see langword="null"/> so the caller's UI can render an empty
/// avatar slot without crashing.
/// </summary>
public sealed class AvatarService
{
    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly Dictionary<string, Bitmap> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<Bitmap?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sentinel key for the default Steve head — also doubles as Minotar's URL slug.</summary>
    private const string SteveKey = "MHF_Steve";

    public AvatarService(HttpClient http)
    {
        _http = http;
        _cacheDir = Path.Combine(Paths.Data, "ui", "avatars");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// Resolves the 24×24 helm-overlay head for the given Minecraft UUID. Hits
    /// the in-memory cache → on-disk cache → Minotar in that order. Returns
    /// <see langword="null"/> if every layer fails (network down, decode fail,
    /// …) so the caller can leave the avatar slot blank.
    /// </summary>
    public Task<Bitmap?> GetMinecraftHeadAsync(Guid uuid, CancellationToken ct = default)
    {
        var key = uuid.ToString("N");
        // Minotar accepts dashed UUIDs too; pick that form to keep the URL
        // shape consistent with what other launchers send.
        var url = $"https://minotar.net/helm/{uuid:D}/24.png";
        return GetOrFetchAsync(key, url, ct);
    }

    /// <summary>
    /// Returns the default Steve helm head, used for any account without an
    /// online identity (offline mode, no active account, or an instance whose
    /// referenced MS account was signed out).
    /// </summary>
    public Task<Bitmap?> GetSteveHeadAsync(CancellationToken ct = default) =>
        GetOrFetchAsync(SteveKey, $"https://minotar.net/helm/{SteveKey}/24.png", ct);

    /// <summary>
    /// Drops the on-disk + in-memory cache entry for the given UUID. Called
    /// after a per-account sign-out so a shared computer doesn't show the
    /// previous user's head when a different player takes that UUID slot.
    /// </summary>
    public void Invalidate(Guid uuid)
    {
        var key = uuid.ToString("N");
        lock (_memoryCache) _memoryCache.Remove(key);
        try
        {
            var cachePath = Path.Combine(_cacheDir, $"{key}.png");
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
        catch { /* best-effort cache cleanup */ }
    }

    // ---- Internals ----------------------------------------------------------

    private async Task<Bitmap?> GetOrFetchAsync(string key, string url, CancellationToken ct)
    {
        // Memory cache.
        lock (_memoryCache)
        {
            if (_memoryCache.TryGetValue(key, out var cached)) return cached;
        }

        // Coalesce concurrent fetches for the same key into a single HTTP call.
        Task<Bitmap?> task;
        lock (_inFlight)
        {
            if (!_inFlight.TryGetValue(key, out var pending))
            {
                pending = FetchAsync(key, url, ct);
                _inFlight[key] = pending;
            }
            task = pending;
        }

        try { return await task.ConfigureAwait(false); }
        finally
        {
            lock (_inFlight) _inFlight.Remove(key);
        }
    }

    private async Task<Bitmap?> FetchAsync(string key, string url, CancellationToken ct)
    {
        // 1. Disk cache: PNG produced by an earlier successful fetch.
        var cachePath = Path.Combine(_cacheDir, $"{key}.png");
        if (File.Exists(cachePath))
        {
            try
            {
                Bitmap loaded;
                await using (var fs = File.OpenRead(cachePath))
                    loaded = new Bitmap(fs);
                lock (_memoryCache) _memoryCache[key] = loaded;
                return loaded;
            }
            catch
            {
                // Corrupt cache file — fall through to re-fetch. The HTTP
                // write below will overwrite atomically.
            }
        }

        // 2. Network: Minotar's helm endpoint. Minotar gracefully falls back to
        //    Steve for unknown UUIDs, so even a typo'd UUID produces a real PNG.
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            // Persist the raw bytes for next boot before constructing the Bitmap
            // so a failed decode still leaves the file on disk for inspection.
            try { await File.WriteAllBytesAsync(cachePath, bytes, ct).ConfigureAwait(false); }
            catch { /* cache write is best-effort */ }

            Bitmap fresh;
            using (var ms = new MemoryStream(bytes))
                fresh = new Bitmap(ms);

            lock (_memoryCache) _memoryCache[key] = fresh;
            return fresh;
        }
        catch
        {
            // Network failed / parse failed — caller sees null.
            return null;
        }
    }
}
