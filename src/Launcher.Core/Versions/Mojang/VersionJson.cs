using Launcher.Core.Versions.Rules;

namespace Launcher.Core.Versions.Mojang;

public sealed record VersionJson(
    string Id,
    string Type,
    string MainClass,
    string Assets,
    int ComplianceLevel,
    int MinimumLauncherVersion,
    JavaVersionRef JavaVersion,
    AssetIndexRef AssetIndex,
    VersionDownloads Downloads,
    IReadOnlyList<LibraryEntry> Libraries,
    Arguments Arguments,
    LoggingConfig? Logging,
    // Set when this VersionJson is a merge of a base version + overlay (e.g. Fabric).
    // The base version owns the client jar, natives directory, assets — this field
    // tells the launcher to look there rather than under the merged id.
    string? InheritsFrom = null);

public sealed record JavaVersionRef(string Component, int MajorVersion);

public sealed record AssetIndexRef(
    string Id,
    string Sha1,
    long Size,
    long TotalSize,
    Uri Url);

public sealed record VersionDownloads(ClientArtifact Client);

public sealed record ClientArtifact(string Sha1, long Size, Uri Url);

public sealed record LibraryEntry(
    string Name,
    LibraryDownloads? Downloads = null,
    IReadOnlyList<Rule>? Rules = null,
    string? Url = null);

public sealed record LibraryDownloads(ArtifactInfo? Artifact = null);

public sealed record ArtifactInfo(string Path, string Sha1, long Size, Uri Url);

public sealed record Arguments(
    IReadOnlyList<ArgumentToken> Game,
    IReadOnlyList<ArgumentToken> Jvm);

public sealed record LoggingConfig(LoggingClient Client);

public sealed record LoggingClient(string Argument, LoggingFile File, string Type);

public sealed record LoggingFile(string Id, string Sha1, long Size, Uri Url);
