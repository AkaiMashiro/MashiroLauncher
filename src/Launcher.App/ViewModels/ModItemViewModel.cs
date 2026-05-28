using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core.Modloaders.Modrinth;

namespace Launcher.App.ViewModels;

/// <summary>
/// Parent VM hook so each mod card can ask the main VM to install itself
/// without owning the installer or the selected instance.
/// </summary>
public interface IModCommands
{
    Task InstallAsync(ModrinthSearchHit hit);
    void Uninstall(string filename);
    void ShowModDetail(ModrinthSearchHit hit);
    /// <summary>Called by an installed mod card when the user flips its enabled toggle.</summary>
    void ToggleEnabled(InstalledModViewModel mod, bool enable);
    /// <summary>Replace an installed mod with the version stored in <see cref="InstalledModViewModel.AvailableUpdate"/>.</summary>
    Task UpdateModAsync(InstalledModViewModel mod);
}

/// <summary>
/// Wraps one Modrinth search hit for display in the mods category. Loads the
/// icon bitmap lazily in the background so the search results render
/// immediately and icons fade in as they arrive.
/// </summary>
public partial class ModItemViewModel : ObservableObject
{
    private readonly IModCommands _commands;
    public ModrinthSearchHit Hit { get; }

    /// <summary>
    /// True when the search hit advertises the currently-selected instance's
    /// modloader in its <c>categories</c>. Defense in depth — Modrinth's facet
    /// filter (categories:fabric / categories:neoforge) should already exclude
    /// incompatible hits, but if anything slips through we mute the card here
    /// instead of letting the user click "설치" and hit a server error.
    /// </summary>
    public bool IsCompatible { get; }

    public ModItemViewModel(ModrinthSearchHit hit, bool isCompatible, IModCommands commands)
    {
        Hit = hit;
        IsCompatible = isCompatible;
        _commands = commands;
    }

    [ObservableProperty] private Bitmap? _icon;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private bool _isInstalled;

    public string Title => Hit.Title;
    public string Description => Hit.Description;
    public string Author => Hit.Author;
    public string DownloadsLabel => FormatDownloads(Hit.Downloads);

    /// <summary>Four mutually exclusive XAML action-area states.</summary>
    public bool CanInstall   => IsCompatible && !IsInstalling && !IsInstalled;
    public bool ShowIncompatible => !IsCompatible && !IsInstalled;

    partial void OnIsInstallingChanged(bool value) => OnPropertyChanged(nameof(CanInstall));
    partial void OnIsInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(ShowIncompatible));
    }

    [RelayCommand]
    private async Task Install()
    {
        if (!CanInstall) return;
        IsInstalling = true;
        try
        {
            await _commands.InstallAsync(Hit);
            IsInstalled = true;
        }
        finally
        {
            IsInstalling = false;
        }
    }

    /// <summary>Opens the detail overlay so the user can pick a specific version.</summary>
    [RelayCommand]
    private void ShowDetails() => _commands.ShowModDetail(Hit);

    /// <summary>
    /// Fire-and-forget icon download. Called by the parent VM right after the
    /// card is added to the results so the UI can render the text immediately.
    /// </summary>
    public async Task LoadIconAsync(HttpClient http, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Hit.IconUrl)) return;
        try
        {
            using var resp = await http.GetAsync(Hit.IconUrl, ct);
            if (!resp.IsSuccessStatusCode) return;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            await Dispatcher.UIThread.InvokeAsync(() => Icon = bmp);
        }
        catch
        {
            // Icon is best-effort — leave Icon null and the UI shows nothing.
        }
    }

    private static string FormatDownloads(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M 다운로드",
        >= 1_000    => $"{n / 1_000.0:0.#}K 다운로드",
        _           => $"{n} 다운로드",
    };
}
