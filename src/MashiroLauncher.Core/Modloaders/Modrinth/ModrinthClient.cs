using System.Text.Json;

namespace MashiroLauncher.Core.Modloaders.Modrinth;

public class ModrinthException(string message) : Exception(message);

/// <summary>
/// Thin client over api.modrinth.com/v2. Search hits, full project metadata,
/// and per-loader/per-mc-version file listings. No auth required.
/// </summary>
public sealed class ModrinthClient(HttpClient http)
{
    private const string BaseUrl = "https://api.modrinth.com/v2";

    // Modrinth's responses are snake_case; the per-record JsonPropertyName
    // attributes handle the mapping, so we keep options minimal here.
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Search Modrinth for mod projects compatible with the given game version
    /// and loader. <paramref name="query"/> may be empty (returns top mods).
    /// <paramref name="offset"/> drives pagination — pass 0 for the first page,
    /// then increment by <paramref name="limit"/> for each "load more" click.
    /// <paramref name="sortIndex"/> is one of Modrinth's accepted index values
    /// (relevance, downloads, follows, newest, updated); defaults to downloads
    /// so an empty query returns top mods.
    /// </summary>
    public async Task<ModrinthSearchResult> SearchAsync(
        string query, string mcVersion, string loader,
        int limit = 20, int offset = 0, string sortIndex = "downloads",
        CancellationToken ct = default)
    {
        // facets is a nested-array JSON literal that Modrinth ANDs across the
        // outer arrays and ORs within each inner array.
        var facets = JsonSerializer.Serialize(new[]
        {
            new[] { $"versions:{mcVersion}" },
            new[] { $"categories:{loader}" },
            new[] { "project_type:mod" },
        });
        var url =
            $"{BaseUrl}/search"
            + $"?query={Uri.EscapeDataString(query ?? "")}"
            + $"&facets={Uri.EscapeDataString(facets)}"
            + $"&limit={limit}"
            + $"&offset={offset}"
            + $"&index={Uri.EscapeDataString(sortIndex)}";

        return await GetJsonAsync<ModrinthSearchResult>(url, ct);
    }

    public Task<ModrinthProject> GetProjectAsync(string idOrSlug, CancellationToken ct = default) =>
        GetJsonAsync<ModrinthProject>($"{BaseUrl}/project/{idOrSlug}", ct);

    /// <summary>Fetch one specific version by id (used when a user picks a version from the detail view).</summary>
    public Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken ct = default) =>
        GetJsonAsync<ModrinthVersion>($"{BaseUrl}/version/{versionId}", ct);

    /// <summary>
    /// Versions of a project that work with the given mc version + loader,
    /// newest-published first (Modrinth's default order).
    /// </summary>
    public async Task<List<ModrinthVersion>> GetVersionsAsync(
        string idOrSlug, string mcVersion, string loader, CancellationToken ct = default)
    {
        var gameVersions = JsonSerializer.Serialize(new[] { mcVersion });
        var loaders = JsonSerializer.Serialize(new[] { loader });
        var url =
            $"{BaseUrl}/project/{idOrSlug}/version"
            + $"?game_versions={Uri.EscapeDataString(gameVersions)}"
            + $"&loaders={Uri.EscapeDataString(loaders)}";
        return await GetJsonAsync<List<ModrinthVersion>>(url, ct);
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // Modrinth asks third parties to identify themselves; helps with
        // rate-limit prioritisation.
        req.Headers.UserAgent.ParseAdd("MashiroLauncher/0.1 (github.com/AkaiMashiro/MashiroLauncher)");

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new ModrinthException($"Modrinth {(int)resp.StatusCode}: {body}");

        return JsonSerializer.Deserialize<T>(body, Opts)
               ?? throw new ModrinthException($"빈 응답 또는 파싱 실패: {url}");
    }
}
