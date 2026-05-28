namespace Launcher.Core.Java;

public sealed record JreOsEntry(
    JreAvailability Availability,
    JreManifestRef Manifest,
    JreVersionInfo Version);

public sealed record JreAvailability(int Group, int Progress);

public sealed record JreManifestRef(string Sha1, long Size, Uri Url);

public sealed record JreVersionInfo(string Name, DateTimeOffset Released);

public sealed record JreFileManifest(IReadOnlyDictionary<string, JreFile> Files);

public sealed record JreFile(
    string Type,
    bool? Executable = null,
    JreFileDownloads? Downloads = null,
    string? Target = null);

public sealed record JreFileDownloads(JreDownload? Raw = null, JreDownload? Lzma = null);

public sealed record JreDownload(string Sha1, long Size, Uri Url);
