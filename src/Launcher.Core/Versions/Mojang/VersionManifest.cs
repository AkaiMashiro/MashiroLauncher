namespace Launcher.Core.Versions.Mojang;

public sealed record VersionManifest(
    LatestVersions Latest,
    IReadOnlyList<VersionManifestEntry> Versions);

public sealed record LatestVersions(string Release, string Snapshot);

public sealed record VersionManifestEntry(
    string Id,
    string Type,
    Uri Url,
    DateTimeOffset Time,
    DateTimeOffset ReleaseTime,
    string Sha1,
    int ComplianceLevel);
