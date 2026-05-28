using MashiroLauncher.Core.Common;
using MashiroLauncher.Core.Versions.Mojang;

namespace MashiroLauncher.Core.Modloaders;

/// <summary>
/// Layers a modloader's profile (Fabric or NeoForge) on top of a vanilla
/// <see cref="VersionJson"/>, producing a merged spec that
/// <see cref="Launching.ArgumentBuilder"/> can hand to the JVM.
///
/// The merge rule for libraries is "overlay wins on collision, vanilla keeps
/// classifier variants" — see <see cref="MavenCoordinate.DedupeKey"/>. Args
/// are simply concatenated (vanilla first, overlay second) so loader-supplied
/// JVM flags can override earlier defaults.
/// </summary>
public static class ModloaderOverlayMerger
{
    public static VersionJson Merge(VersionJson vanilla, IModloaderOverlayProfile overlay)
    {
        return vanilla with
        {
            Id = overlay.Id,
            Type = overlay.Type,
            MainClass = overlay.MainClass,
            InheritsFrom = overlay.InheritsFrom,
            Libraries = MergeLibraries(vanilla.Libraries, overlay.Libraries),
            Arguments = MergeArguments(vanilla.Arguments, overlay.Arguments),
        };
    }

    // Overlay wins on collisions. Key respects classifier so native variants
    // (which usually only exist on vanilla side) aren't accidentally removed.
    private static IReadOnlyList<LibraryEntry> MergeLibraries(
        IReadOnlyList<LibraryEntry> vanilla, IReadOnlyList<LibraryEntry> overlay)
    {
        var byKey = new Dictionary<string, LibraryEntry>();
        foreach (var lib in vanilla) byKey[MavenCoordinate.DedupeKey(lib.Name)] = lib;
        foreach (var lib in overlay) byKey[MavenCoordinate.DedupeKey(lib.Name)] = lib;
        return [.. byKey.Values];
    }

    private static Arguments MergeArguments(Arguments vanilla, Arguments? overlay)
    {
        if (overlay is null) return vanilla;
        return new Arguments(
            Game: [.. vanilla.Game, .. overlay.Game],
            Jvm:  [.. vanilla.Jvm,  .. overlay.Jvm]);
    }
}
