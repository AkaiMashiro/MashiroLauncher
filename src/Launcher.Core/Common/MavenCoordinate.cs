namespace Launcher.Core.Common;

public sealed record MavenCoordinate(
    string GroupId,
    string ArtifactId,
    string Version,
    string? Classifier = null,
    string Extension = "jar")
{
    public static MavenCoordinate Parse(string spec)
    {
        ArgumentException.ThrowIfNullOrEmpty(spec);

        var body = spec;
        var extension = "jar";
        var atIndex = spec.IndexOf('@');
        if (atIndex >= 0)
        {
            extension = spec[(atIndex + 1)..];
            body = spec[..atIndex];
            if (extension.Length == 0)
                throw new FormatException($"Empty extension after '@' in Maven coordinate: '{spec}'");
        }

        var parts = body.Split(':');
        if (parts.Length is < 3 or > 4)
            throw new FormatException($"Maven coordinate must have 3 or 4 colon-separated parts: '{spec}'");
        if (parts.Any(string.IsNullOrEmpty))
            throw new FormatException($"Maven coordinate has empty component: '{spec}'");

        return new MavenCoordinate(
            GroupId: parts[0],
            ArtifactId: parts[1],
            Version: parts[2],
            Classifier: parts.Length == 4 ? parts[3] : null,
            Extension: extension);
    }

    public string ToPath()
    {
        var fileName = Classifier is null
            ? $"{ArtifactId}-{Version}.{Extension}"
            : $"{ArtifactId}-{Version}-{Classifier}.{Extension}";
        return $"{GroupId.Replace('.', '/')}/{ArtifactId}/{Version}/{fileName}";
    }

    // Dedupe key for library lists: ignores version (so Fabric's ASM replaces vanilla's),
    // but preserves classifier (so LWJGL's natives-* variants coexist with the main jar).
    public static string DedupeKey(string mavenName)
    {
        var parts = mavenName.Split(':');
        if (parts.Length < 2) return mavenName;
        var key = $"{parts[0]}:{parts[1]}";
        if (parts.Length >= 4) key += $":{parts[3]}";
        return key;
    }
}
