using Launcher.Core.Common;
using Launcher.Core.Versions.Mojang;
using Launcher.Core.Versions.Rules;

namespace Launcher.Core.Versions.Libraries;

public sealed record PlannedLibrary(string Name, ArtifactInfo Artifact, string LocalPath);

public static class LibraryPlanner
{
    // Local paths only — used by ArgumentBuilder for classpath construction.
    // Synchronous because it does no I/O; the artifact files are expected to exist on disk
    // (already installed by PlanAsync via InstallService).
    public static IReadOnlyList<string> ClasspathPaths(
        IReadOnlyList<LibraryEntry> libraries, RuleContext ctx)
    {
        var paths = new List<string>();
        foreach (var lib in Filter(libraries, ctx))
        {
            var rel = RelativePathOf(lib);
            if (rel is null) continue;
            paths.Add(Paths.LibraryPath(rel));
        }
        return paths;
    }

    // Resolves full download info. For Mojang-format entries, info is inline.
    // For Fabric-format Maven entries, the .sha1 sidecar is fetched in parallel.
    public static async Task<IReadOnlyList<PlannedLibrary>> PlanAsync(
        IReadOnlyList<LibraryEntry> libraries,
        RuleContext ctx,
        Downloader downloader,
        CancellationToken ct = default)
    {
        var filtered = Filter(libraries, ctx).ToList();
        var tasks = filtered.Select(lib => ResolveAsync(lib, downloader, ct));
        var resolved = await Task.WhenAll(tasks);
        return [.. resolved.Where(r => r is not null).Cast<PlannedLibrary>()];
    }

    // Dedupe key includes classifier — so an LWJGL no-classifier jar and its
    // natives-* siblings stay distinct, but two entries for the same coordinate
    // (e.g. ASM from vanilla and Fabric) collapse with overlay winning.
    private static IEnumerable<LibraryEntry> Filter(IReadOnlyList<LibraryEntry> libs, RuleContext ctx)
    {
        var byKey = new Dictionary<string, LibraryEntry>();
        foreach (var lib in libs)
        {
            if (!RuleEvaluator.Evaluate(lib.Rules, ctx)) continue;
            byKey[MavenCoordinate.DedupeKey(lib.Name)] = lib;
        }
        return byKey.Values;
    }

    private static string? RelativePathOf(LibraryEntry lib)
    {
        if (lib.Downloads?.Artifact is { } art) return art.Path;
        if (lib.Url is not null) return MavenCoordinate.Parse(lib.Name).ToPath();
        return null;
    }

    private static async Task<PlannedLibrary?> ResolveAsync(
        LibraryEntry lib, Downloader downloader, CancellationToken ct)
    {
        if (lib.Downloads?.Artifact is { } art)
            return new PlannedLibrary(lib.Name, art, Paths.LibraryPath(art.Path));

        if (lib.Url is { } mavenRoot)
        {
            var coord = MavenCoordinate.Parse(lib.Name);
            var path = coord.ToPath();
            var baseUri = new Uri(mavenRoot.EndsWith('/') ? mavenRoot : mavenRoot + "/");
            var jarUri = new Uri(baseUri, path);
            var sha1Uri = new Uri(jarUri + ".sha1");
            var sha1Text = await downloader.FetchTextAsync(sha1Uri, ct);
            // .sha1 sidecar is typically "<hex>  <filename>" or just "<hex>"
            var sha1 = sha1Text.Trim().Split(' ', 2)[0];
            var artifact = new ArtifactInfo(path, sha1, 0, jarUri);
            return new PlannedLibrary(lib.Name, artifact, Paths.LibraryPath(path));
        }

        return null;
    }
}
