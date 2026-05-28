using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core.Modloaders.Modrinth;

namespace Launcher.App.ViewModels;

/// <summary>
/// Backs the overlay shown when the user clicks 자세히 on a mod card.
/// Holds the long-form description + per-instance compatible version list,
/// and exposes a single Install command that pins the user's pick.
///
/// This VM is the only piece that knows about <see cref="ModrinthClient"/>;
/// the parent (<see cref="MainWindowViewModel"/>) just constructs us with
/// the hit and the loader/version context, then displays whatever we hold.
/// </summary>
public partial class ModDetailViewModel : ObservableObject
{
    private readonly IModDetailCommands _commands;
    private readonly HttpClient _http;
    private readonly ModrinthClient _client;
    private readonly string _mcVersion;
    private readonly string _loader;

    public string ProjectId { get; }
    public string Title { get; }
    public string Author { get; }
    public string? IconUrl { get; }

    public ModDetailViewModel(
        ModrinthSearchHit hit,
        ModrinthClient client,
        HttpClient http,
        string mcVersion,
        string loader,
        bool isInitiallyInstalled,
        IModDetailCommands commands)
    {
        ProjectId = hit.ProjectId;
        Title = hit.Title;
        Author = hit.Author;
        IconUrl = hit.IconUrl;
        _description = hit.Description;  // start with the short blurb, swap to long body when loaded
        _client = client;
        _http = http;
        _mcVersion = mcVersion;
        _loader = loader;
        _isInstalled = isInitiallyInstalled;
        _commands = commands;
    }

    [ObservableProperty] private Bitmap? _icon;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _categoriesLabel = "";
    [ObservableProperty] private string _updatedLabel = "";
    [ObservableProperty] private ModrinthVersion? _selectedVersion;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private bool _isInstalled;

    public ObservableCollection<ModrinthVersion> Versions { get; } = [];

    public bool HasNoCompatibleVersion => !IsLoading && Versions.Count == 0;
    public bool CanInstallSelected => SelectedVersion is not null && !IsInstalling && !IsInstalled;

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(HasNoCompatibleVersion));
    partial void OnSelectedVersionChanged(ModrinthVersion? value) => OnPropertyChanged(nameof(CanInstallSelected));
    partial void OnIsInstallingChanged(bool value) => OnPropertyChanged(nameof(CanInstallSelected));
    partial void OnIsInstalledChanged(bool value) => OnPropertyChanged(nameof(CanInstallSelected));

    /// <summary>
    /// Loads icon, full project body, and the list of compatible versions in
    /// parallel. Failures are absorbed — the overlay still shows whatever
    /// metadata we already had from the search hit.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var iconTask = LoadIconAsync(ct);
            var projectTask = SafeProjectAsync(ct);
            var versionsTask = SafeVersionsAsync(ct);
            await Task.WhenAll(iconTask, projectTask, versionsTask);

            var project = projectTask.Result;
            if (project is not null)
            {
                if (!string.IsNullOrWhiteSpace(project.Body))
                    Description = project.Body!;
                if (project.Categories is { Count: > 0 })
                    CategoriesLabel = string.Join(" · ", project.Categories);
                if (project.Updated is { } updated)
                    UpdatedLabel = $"업데이트: {updated.LocalDateTime:yyyy-MM-dd}";
            }

            var versions = versionsTask.Result;
            foreach (var v in versions) Versions.Add(v);
            // Modrinth's list is newest-first; default to that so the typical
            // "install latest" flow takes one click from the detail view too.
            SelectedVersion = Versions.FirstOrDefault();
            OnPropertyChanged(nameof(HasNoCompatibleVersion));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadIconAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(IconUrl)) return;
        try
        {
            using var resp = await _http.GetAsync(IconUrl, ct);
            if (!resp.IsSuccessStatusCode) return;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            await Dispatcher.UIThread.InvokeAsync(() => Icon = bmp);
        }
        catch { /* best-effort */ }
    }

    private async Task<ModrinthProject?> SafeProjectAsync(CancellationToken ct)
    {
        try { return await _client.GetProjectAsync(ProjectId, ct); }
        catch { return null; }
    }

    private async Task<List<ModrinthVersion>> SafeVersionsAsync(CancellationToken ct)
    {
        try { return await _client.GetVersionsAsync(ProjectId, _mcVersion, _loader, ct); }
        catch { return new(); }
    }

    [RelayCommand]
    private async Task InstallSelectedAsync()
    {
        if (!CanInstallSelected || SelectedVersion is null) return;
        IsInstalling = true;
        try
        {
            await _commands.InstallVersionAsync(ProjectId, SelectedVersion);
            IsInstalled = true;
        }
        finally
        {
            IsInstalling = false;
        }
    }

    [RelayCommand]
    private void Close() => _commands.CloseModDetail();
}

/// <summary>Parent hook so the detail VM can stay decoupled from MainWindowViewModel.</summary>
public interface IModDetailCommands
{
    Task InstallVersionAsync(string projectId, ModrinthVersion version);
    void CloseModDetail();
}
