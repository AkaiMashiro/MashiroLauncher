using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MashiroLauncher.Core.Modloaders.Modrinth;

namespace MashiroLauncher.App.ViewModels;

/// <summary>
/// One entry in the "installed mods" list for the currently selected instance.
/// Knows two flavors of mod:
///   - Tracked (ProjectId is non-null) — installed via Modrinth, we'll lazily
///     fill in the friendly title + icon from the API.
///   - External (ProjectId is null) — a .jar the user dropped in manually.
///     Title falls back to the filename, icon stays as the "?" placeholder.
/// </summary>
public partial class InstalledModViewModel : ObservableObject
{
    private readonly IModCommands _commands;

    public string Filename { get; private set; }
    public string? ProjectId { get; }

    public InstalledModViewModel(string filename, string? projectId, bool isEnabled, IModCommands commands)
    {
        Filename = filename;
        ProjectId = projectId;
        _isEnabled = isEnabled;
        _commands = commands;
        // Start with a display name derived from the filename (with the
        // .disabled suffix stripped); LoadMetadataAsync will replace it for
        // tracked mods.
        _title = StripDisabled(filename);
    }

    [ObservableProperty] private string _title;
    [ObservableProperty] private Bitmap? _icon;
    [ObservableProperty] private bool _isEnabled;

    /// <summary>Set by the parent VM after a successful update check.
    /// Non-null means a newer compatible version is on Modrinth.</summary>
    [ObservableProperty] private ModrinthVersion? _availableUpdate;

    public bool HasIcon => Icon is not null;
    public bool IsExternal => ProjectId is null;
    public bool HasUpdate => AvailableUpdate is not null && !IsUpdating;

    public string UpdateLabel => AvailableUpdate is { } v
        ? $"업데이트: {v.VersionNumber}"
        : "";

    [ObservableProperty] private bool _isUpdating;

    partial void OnAvailableUpdateChanged(ModrinthVersion? value)
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateLabel));
    }

    partial void OnIsUpdatingChanged(bool value) => OnPropertyChanged(nameof(HasUpdate));

    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (AvailableUpdate is null || IsUpdating) return;
        IsUpdating = true;
        try
        {
            await _commands.UpdateModAsync(this);
        }
        finally
        {
            IsUpdating = false;
        }
    }

    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));

    partial void OnIsEnabledChanged(bool value)
    {
        // Defer the rename to the parent VM so the installer (which knows the
        // instance id) handles the file move. The parent calls back into
        // ApplyRename below with the new on-disk filename.
        _commands.ToggleEnabled(this, value);
    }

    /// <summary>
    /// Called by the parent VM after a successful rename to refresh the
    /// stored filename. Updates without re-triggering OnIsEnabledChanged.
    /// </summary>
    public void ApplyRename(string newFilename) => Filename = newFilename;

    /// <summary>
    /// Revert the toggle without going through the OnIsEnabledChanged path
    /// (avoids the toggle → command → revert infinite loop on failure).
    /// We deliberately bypass the generated setter — MVVMTK0034 is suppressed
    /// because skipping the change hook is the whole point of this method.
    /// </summary>
    public void ApplyEnabledState(bool enabled)
    {
#pragma warning disable MVVMTK0034
        if (_isEnabled == enabled) return;
        _isEnabled = enabled;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(IsEnabled));
    }

    private static string StripDisabled(string filename) =>
        filename.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
            ? filename[..^".disabled".Length]
            : filename;

    /// <summary>
    /// Resolve project title + icon from Modrinth in the background. Called
    /// once after the card is added to the list; failures are silent so a
    /// stale id or a flaky network just leaves the card showing its filename.
    /// </summary>
    public async Task LoadMetadataAsync(ModrinthClient client, HttpClient http, CancellationToken ct = default)
    {
        if (ProjectId is null) return;
        try
        {
            var project = await client.GetProjectAsync(ProjectId, ct);
            await Dispatcher.UIThread.InvokeAsync(() => Title = project.Title);

            if (!string.IsNullOrEmpty(project.IconUrl))
            {
                using var resp = await http.GetAsync(project.IconUrl, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    using var ms = new MemoryStream(bytes);
                    var bmp = new Bitmap(ms);
                    await Dispatcher.UIThread.InvokeAsync(() => Icon = bmp);
                }
            }
        }
        catch
        {
            // Best-effort enrichment.
        }
    }

    [RelayCommand]
    private void Uninstall() => _commands.Uninstall(Filename);
}
