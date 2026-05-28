using System.Text.Json.Serialization;

namespace Launcher.Core.Modloaders.Modrinth;

// Modrinth API responses use snake_case; we map every field explicitly so we
// don't depend on JsonSerializerOptions naming policy.

public sealed record ModrinthSearchResult(
    [property: JsonPropertyName("hits")] List<ModrinthSearchHit> Hits,
    [property: JsonPropertyName("total_hits")] int TotalHits);

public sealed record ModrinthSearchHit(
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("icon_url")] string? IconUrl,
    [property: JsonPropertyName("downloads")] int Downloads,
    [property: JsonPropertyName("author")] string Author,
    // Mixed list of loader tags ("fabric", "neoforge", …) and content categories
    // ("magic", "technology", …). Used by the UI to mark a hit as
    // incompatible with the currently-selected instance's modloader, even
    // though the SearchAsync facet should already exclude those.
    [property: JsonPropertyName("categories")] List<string>? Categories = null);

public sealed record ModrinthProject(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("icon_url")] string? IconUrl,
    [property: JsonPropertyName("versions")] List<string> VersionIds,
    // Body is the long-form markdown description shown on the project page.
    // Categories + Updated drive the detail-view metadata strip.
    [property: JsonPropertyName("body")] string? Body = null,
    [property: JsonPropertyName("categories")] List<string>? Categories = null,
    [property: JsonPropertyName("updated")] DateTimeOffset? Updated = null);

public sealed record ModrinthVersion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version_number")] string VersionNumber,
    [property: JsonPropertyName("game_versions")] List<string> GameVersions,
    [property: JsonPropertyName("loaders")] List<string> Loaders,
    [property: JsonPropertyName("files")] List<ModrinthFile> Files,
    [property: JsonPropertyName("dependencies")] List<ModrinthDependency> Dependencies,
    [property: JsonPropertyName("date_published")] DateTimeOffset DatePublished);

public sealed record ModrinthFile(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("primary")] bool Primary,
    [property: JsonPropertyName("hashes")] Dictionary<string, string> Hashes,
    [property: JsonPropertyName("size")] long Size);

public sealed record ModrinthDependency(
    [property: JsonPropertyName("project_id")] string? ProjectId,
    [property: JsonPropertyName("version_id")] string? VersionId,
    [property: JsonPropertyName("dependency_type")] string DependencyType);
