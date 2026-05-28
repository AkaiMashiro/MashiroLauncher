namespace Launcher.Core.Common;

/// <summary>
/// Seeds a fresh instance's game directory with settings from the user's
/// existing vanilla Minecraft install (%APPDATA%/.minecraft on Windows, the
/// equivalents on macOS/Linux). We only copy the few files that carry user
/// preference (options.txt for video/audio/keybinds, servers.dat for the
/// multiplayer server list) — never worlds, resourcepacks, or mods.
///
/// Copies are non-overwriting: if the target instance already has a file we
/// leave the user's value alone.
/// </summary>
public static class VanillaImporter
{
    /// <summary>Files we attempt to copy. Anything else is left to vanilla defaults.</summary>
    private static readonly string[] FilesToImport = ["options.txt", "servers.dat"];

    public static string VanillaPath
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    ".minecraft");
            if (OperatingSystem.IsMacOS())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "minecraft");
            // Linux / fallback
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".minecraft");
        }
    }

    public static bool VanillaExists() => Directory.Exists(VanillaPath);

    /// <summary>
    /// Copy options.txt / servers.dat from the user's vanilla install into the
    /// instance's game directory, if they exist and aren't already present.
    /// Returns the list of files that were actually copied (useful for status messages).
    /// </summary>
    public static IReadOnlyList<string> SeedInstance(string instanceGameDir)
    {
        if (!VanillaExists()) return Array.Empty<string>();
        Directory.CreateDirectory(instanceGameDir);

        var copied = new List<string>();
        foreach (var name in FilesToImport)
        {
            var src = Path.Combine(VanillaPath, name);
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(instanceGameDir, name);
            if (File.Exists(dst)) continue;  // never overwrite user data
            try
            {
                File.Copy(src, dst, overwrite: false);
                copied.Add(name);
            }
            catch
            {
                // Best-effort — a locked file or perms issue shouldn't stop instance creation.
            }
        }
        return copied;
    }
}
