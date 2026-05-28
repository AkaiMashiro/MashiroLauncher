using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MashiroLauncher.Core.Instances;
using MashiroLauncher.Core.Launching;

namespace MashiroLauncher.App.ViewModels;

/// <summary>
/// Bridge MainWindowViewModel exposes to each card so they can request
/// destructive/expensive operations without owning the storage layer.
/// </summary>
public interface IInstanceCommands
{
    void OpenFolder(Instance instance);
    Task DuplicateAsync(Instance instance);
    void Rename(Instance instance, string newName);
    void Delete(Instance instance);
    Task LaunchAsync(Instance instance);
    /// <summary>Persist per-instance JVM overrides (memory + custom args). Null values mean "inherit global".</summary>
    void UpdateJvmOverrides(Instance instance, int? minMb, int? maxMb, string? customArgs);
    /// <summary>Zip up the whole instance directory and let the user save it somewhere.</summary>
    Task ExportAsync(Instance instance);
}

/// <summary>
/// Wraps a single <see cref="Instance"/> for the settings list. Owns its own
/// transient UI state (rename input, delete-confirmation flag) so multiple
/// cards can be in different modes at the same time.
/// </summary>
public partial class InstanceItemViewModel : ObservableObject
{
    private readonly IInstanceCommands _commands;

    public Instance Model { get; private set; }

    public InstanceItemViewModel(Instance model, IInstanceCommands commands)
    {
        Model = model;
        _commands = commands;
        _renameInput = model.Name;
    }

    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private bool _isConfiguringJvm;
    [ObservableProperty] private string _renameInput;

    // Inputs for the JVM-override form. Strings so we can distinguish empty
    // ("clear override") from invalid ("ignore"), and so binding survives
    // partial typing.
    [ObservableProperty] private string _minMemoryInput = "";
    [ObservableProperty] private string _maxMemoryInput = "";
    [ObservableProperty] private string _customJvmArgsInput = "";

    /// <summary>True when the card shows the normal info+actions row (no inline form open).</summary>
    public bool IsDefaultMode => !IsRenaming && !IsConfirmingDelete && !IsConfiguringJvm;

    partial void OnIsRenamingChanged(bool value)         => OnPropertyChanged(nameof(IsDefaultMode));
    partial void OnIsConfirmingDeleteChanged(bool value) => OnPropertyChanged(nameof(IsDefaultMode));
    partial void OnIsConfiguringJvmChanged(bool value)   => OnPropertyChanged(nameof(IsDefaultMode));

    public string Name => Model.Name;
    public string VersionId => Model.VersionId;
    public string ModloaderLabel => Model.Modloader switch
    {
        Modloader.Fabric   => "Fabric",
        Modloader.NeoForge => "NeoForge",
        _                  => "Vanilla",
    };

    public string LastPlayedLabel => Model.LastPlayedAt is null
        ? "한 번도 플레이하지 않음"
        : $"마지막 플레이: {FormatRelative(Model.LastPlayedAt.Value)}";

    /// <summary>Small hint shown on the default card row when this instance has
    /// per-instance JVM overrides set. Empty string when everything inherits global.</summary>
    public string JvmHint
    {
        get
        {
            var parts = new List<string>();
            if (Model.MinMemoryMb is not null || Model.MaxMemoryMb is not null)
            {
                var min = Model.MinMemoryMb?.ToString() ?? "기본";
                var max = Model.MaxMemoryMb?.ToString() ?? "기본";
                parts.Add($"메모리 {min}–{max}MB");
            }
            if (!string.IsNullOrWhiteSpace(Model.CustomJvmArgs))
                parts.Add("커스텀 JVM args");
            return parts.Count == 0 ? "" : string.Join(" · ", parts);
        }
    }

    public bool HasJvmOverrides => !string.IsNullOrEmpty(JvmHint);

    /// <summary>
    /// Called by the parent VM after it mutates the underlying model so the card
    /// re-renders without us having to rebuild the whole collection.
    /// </summary>
    public void UpdateModel(Instance model)
    {
        Model = model;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(VersionId));
        OnPropertyChanged(nameof(ModloaderLabel));
        OnPropertyChanged(nameof(LastPlayedLabel));
        OnPropertyChanged(nameof(JvmHint));
        OnPropertyChanged(nameof(HasJvmOverrides));
    }

    [RelayCommand]
    private async Task Play() => await _commands.LaunchAsync(Model);

    [RelayCommand]
    private void OpenFolder() => _commands.OpenFolder(Model);

    [RelayCommand]
    private async Task Duplicate() => await _commands.DuplicateAsync(Model);

    [RelayCommand]
    private async Task Export() => await _commands.ExportAsync(Model);

    [RelayCommand]
    private void BeginRename()
    {
        IsConfirmingDelete = false;
        RenameInput = Model.Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void ConfirmRename()
    {
        var newName = RenameInput?.Trim();
        if (!string.IsNullOrEmpty(newName) && newName != Model.Name)
            _commands.Rename(Model, newName);
        IsRenaming = false;
    }

    [RelayCommand]
    private void CancelRename()
    {
        RenameInput = Model.Name;
        IsRenaming = false;
    }

    [RelayCommand]
    private void BeginDelete()
    {
        IsRenaming = false;
        IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void ConfirmDelete()
    {
        _commands.Delete(Model);
        IsConfirmingDelete = false;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    // ---- JVM override sub-form -------------------------------------------

    [RelayCommand]
    private void BeginConfigureJvm()
    {
        IsRenaming = false;
        IsConfirmingDelete = false;
        MinMemoryInput      = Model.MinMemoryMb?.ToString() ?? "";
        MaxMemoryInput      = Model.MaxMemoryMb?.ToString() ?? "";
        CustomJvmArgsInput  = Model.CustomJvmArgs ?? "";
        IsConfiguringJvm = true;
    }

    [RelayCommand]
    private void CancelJvmConfig() => IsConfiguringJvm = false;

    [RelayCommand]
    private void SaveJvmConfig()
    {
        // Empty inputs → null override (inherit global). Bad numeric inputs
        // also map to null so the form can't poison a launch — the field stays
        // visible so the user can fix it later.
        int? min = TryParseMemory(MinMemoryInput);
        int? max = TryParseMemory(MaxMemoryInput);
        string? args = string.IsNullOrWhiteSpace(CustomJvmArgsInput) ? null : CustomJvmArgsInput.Trim();

        _commands.UpdateJvmOverrides(Model, min, max, args);
        IsConfiguringJvm = false;
    }

    private static int? TryParseMemory(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!int.TryParse(raw.Trim(), out var n)) return null;
        return n > 0 ? n : null;
    }

    private static string FormatRelative(DateTimeOffset when)
    {
        var diff = DateTimeOffset.Now - when;
        if (diff < TimeSpan.FromMinutes(1)) return "방금 전";
        if (diff < TimeSpan.FromHours(1))   return $"{(int)diff.TotalMinutes}분 전";
        if (diff < TimeSpan.FromDays(1))    return $"{(int)diff.TotalHours}시간 전";
        if (diff < TimeSpan.FromDays(30))   return $"{(int)diff.TotalDays}일 전";
        if (diff < TimeSpan.FromDays(365))  return $"{(int)(diff.TotalDays / 30)}개월 전";
        return when.LocalDateTime.ToString("yyyy-MM-dd");
    }
}
