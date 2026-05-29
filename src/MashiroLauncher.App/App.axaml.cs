using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MashiroLauncher.App.Services;
using MashiroLauncher.App.ViewModels;
using MashiroLauncher.Core.Common;

namespace MashiroLauncher.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Paths.EnsureBaseDirectories();
            Directory.CreateDirectory(Path.Combine(Paths.Data, "ui"));

            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MashiroLauncher/0.1");

            var downloader = new Downloader(http);
            var statusService = new MojangStatusService(http);
            var backgroundService = new BackgroundService();
            var launchService = new UiLaunchService(downloader);
            var msAuthService = new MicrosoftAuthService(http);
            var updateService = new UpdateService(http);
            var avatarService = new AvatarService(http);

            var window = new MainWindow();
            var viewModel = new MainWindowViewModel(
                downloader,
                http,
                statusService,
                backgroundService,
                launchService,
                msAuthService,
                updateService,
                avatarService,
                pickImageAsync: () => PickImageAsync(window),
                pickModFilesAsync: () => PickJarFilesAsync(window),
                pickMrpackAsync: () => PickMrpackAsync(window),
                pickInstanceZipSaveAsync: name => PickInstanceZipSaveAsync(window, name),
                pickInstanceZipOpenAsync: () => PickInstanceZipOpenAsync(window));
            window.DataContext = viewModel;
            desktop.MainWindow = window;

            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task<string?> PickImageAsync(Window window)
    {
        var storage = TopLevel.GetTopLevel(window)?.StorageProvider;
        if (storage is null) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose background image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp"],
                },
            ],
        });
        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    /// <summary>
    /// Multi-select file picker used by Settings → 모드 → "외부 jar 가져오기" so the
    /// user can copy Modrinth-less mod jars into the instance's mods folder.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> PickJarFilesAsync(Window window)
    {
        var storage = TopLevel.GetTopLevel(window)?.StorageProvider;
        if (storage is null) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "외부 모드 .jar 선택",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Jar 모드 파일") { Patterns = ["*.jar"] },
            ],
        });
        if (files.Count == 0) return null;
        return files.Select(f => f.Path.LocalPath).ToList();
    }

    /// <summary>
    /// Single-select picker for a Modrinth modpack (.mrpack) → used by Settings
    /// → 인스턴스 → "+ 모드팩 가져오기" to bootstrap a new instance from a pack.
    /// </summary>
    private static async Task<string?> PickMrpackAsync(Window window)
    {
        var storage = TopLevel.GetTopLevel(window)?.StorageProvider;
        if (storage is null) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Modrinth 모드팩 (.mrpack) 선택",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Modrinth 모드팩") { Patterns = ["*.mrpack"] },
            ],
        });
        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    /// <summary>Save-file picker for "인스턴스 내보내기" (.zip).</summary>
    private static async Task<string?> PickInstanceZipSaveAsync(Window window, string suggestedName)
    {
        var storage = TopLevel.GetTopLevel(window)?.StorageProvider;
        if (storage is null) return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "인스턴스 백업 저장",
            SuggestedFileName = suggestedName,
            DefaultExtension = "zip",
            FileTypeChoices =
            [
                new FilePickerFileType("Zip 백업") { Patterns = ["*.zip"] },
            ],
        });
        return file?.Path.LocalPath;
    }

    /// <summary>Open-file picker for "인스턴스 가져오기" (.zip).</summary>
    private static async Task<string?> PickInstanceZipOpenAsync(Window window)
    {
        var storage = TopLevel.GetTopLevel(window)?.StorageProvider;
        if (storage is null) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "인스턴스 백업 가져오기",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Mashiro 인스턴스 백업") { Patterns = ["*.zip"] },
            ],
        });
        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }
}
