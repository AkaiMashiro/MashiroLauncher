using MashiroLauncher.Core.Versions.Mojang;

namespace MashiroLauncher.Core.Modloaders;

/// <summary>
/// Shape common to Fabric and NeoForge "profile" JSONs: a lightweight overlay
/// that inherits the heavy fields (client jar, assets, javaVersion) from a
/// vanilla version and contributes its own id / mainClass / libraries / args.
///
/// Both <see cref="Fabric.FabricProfile"/> and
/// <see cref="NeoForge.NeoForgeProfile"/> implement this; the shared merger
/// (<see cref="ModloaderOverlayMerger"/>) consumes it without caring which
/// loader produced it.
/// </summary>
public interface IModloaderOverlayProfile
{
    string Id { get; }
    string Type { get; }
    string InheritsFrom { get; }
    string MainClass { get; }
    Arguments? Arguments { get; }
    IReadOnlyList<LibraryEntry> Libraries { get; }
}
