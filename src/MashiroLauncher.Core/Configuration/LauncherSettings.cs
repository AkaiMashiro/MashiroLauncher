using System.Text.Json;

namespace MashiroLauncher.Core.Common;

public sealed record LauncherSettings(
    int MinMemoryMb = 512,
    int MaxMemoryMb = 4096,
    string CustomJvmArgs = "",
    bool UseInstanceMode = false);

public sealed class SettingsStorage
{
    private static string FilePath => Path.Combine(Paths.Data, "settings.json");

    public LauncherSettings Load()
    {
        if (!File.Exists(FilePath)) return new LauncherSettings();
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<LauncherSettings>(json, JsonOptions.Default)
                   ?? new LauncherSettings();
        }
        catch
        {
            return new LauncherSettings();
        }
    }

    public void Save(LauncherSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions.Default));
    }
}
