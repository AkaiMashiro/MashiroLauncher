using MashiroLauncher.Core.Versions.Mojang;

namespace MashiroLauncher.Core.Modloaders.NeoForge;

/// <summary>
/// Subset of NeoForge's installer-emitted <c>neoforge-X.json</c> that we care
/// about — same shape as <see cref="Fabric.FabricProfile"/>. JSON fields we
/// don't read (e.g. NeoForge's own <c>downloads</c> metadata) are silently
/// ignored by the deserializer.
/// </summary>
public sealed record NeoForgeProfile(
    string Id,
    string Type,
    string InheritsFrom,
    string MainClass,
    Arguments? Arguments,
    IReadOnlyList<LibraryEntry> Libraries) : IModloaderOverlayProfile;
