using Launcher.Core.Versions.Mojang;

namespace Launcher.Core.Modloaders.Fabric;

// Lighter DTO than VersionJson — Fabric profiles only declare the overlay fields
// (id, type, mainClass, inheritsFrom, arguments, libraries). Everything else is
// inherited from the base vanilla version JSON.
public sealed record FabricProfile(
    string Id,
    string Type,
    string InheritsFrom,
    string MainClass,
    Arguments? Arguments,
    IReadOnlyList<LibraryEntry> Libraries) : IModloaderOverlayProfile;
