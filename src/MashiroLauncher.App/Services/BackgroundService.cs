using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MashiroLauncher.Core.Common;

namespace MashiroLauncher.App.Services;

public sealed class BackgroundService
{
    private static readonly Uri EmbeddedDefault = new("avares://MashiroLauncher/Assets/background.jpg");

    public static string UserPath { get; } = Path.Combine(Paths.Data, "ui", "background.jpg");

    public Bitmap? LoadCurrent()
    {
        if (File.Exists(UserPath))
        {
            try { return new Bitmap(UserPath); }
            catch { /* fall through to embedded default */ }
        }

        try
        {
            using var stream = AssetLoader.Open(EmbeddedDefault);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(Stream source, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserPath)!);
        var temp = UserPath + ".tmp";
        await using (var dst = File.Create(temp))
            await source.CopyToAsync(dst, ct);
        if (File.Exists(UserPath)) File.Delete(UserPath);
        File.Move(temp, UserPath);
    }
}
