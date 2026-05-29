using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MashiroLauncher.App.Services;
using MashiroLauncher.Core.Auth;
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
    /// <summary>Persist a change to account mode / specific account / offline username for this instance.</summary>
    void UpdateAccountSettings(Instance instance, InstanceAccountMode mode, string? specificAccountId, string? offlineUsername);

    // ---- Avatar pipeline -------------------------------------------------
    //
    // Each card resolves "which account would this instance launch as?" via
    // these hooks rather than re-implementing the priority logic locally. The
    // avatar Bitmap arrives async from Minotar; the caller leaves the avatar
    // slot blank on any failure so the UI never falls back to a placeholder
    // glyph.
    AvatarService AvatarService { get; }
    InstanceAvatarInfo ResolveAvatarInfo(Instance instance);

    /// <summary>Snapshot of every signed-in Microsoft account, used by the per-instance picker.</summary>
    IReadOnlyList<MicrosoftAccount> AvailableMicrosoftAccounts { get; }
}

/// <summary>
/// Lightweight result of "what should an instance card show in the account row?"
/// — a display name plus, when the account is a signed-in Microsoft user, the
/// Minecraft UUID to feed Minotar. Null UUID means "offline / fallback face".
/// </summary>
/// <param name="DisplayName">Username to render next to the avatar.</param>
/// <param name="MinecraftUuid">UUID of the MS account (null for offline / no account).</param>
public sealed record InstanceAvatarInfo(string DisplayName, Guid? MinecraftUuid);

/// <summary>
/// One selectable row in the per-instance account picker (Settings → 인스턴스
/// → 고급). Represents one of three possible launch identities:
/// <list type="bullet">
///   <item><description>Default — use whatever the launcher's active account is.</description></item>
///   <item><description>Offline — offline-mode session with a typed username.</description></item>
///   <item><description>A specific signed-in Microsoft account.</description></item>
/// </list>
/// The avatar bitmap loads asynchronously from Minotar so the picker paints
/// immediately and faces fill in as they arrive.
/// </summary>
public sealed partial class AccountChoice : ObservableObject
{
    public AccountChoice(InstanceAccountMode mode, MicrosoftAccount? account, string label)
    {
        Mode = mode;
        Account = account;
        Label = label;
    }

    public InstanceAccountMode Mode { get; }
    public MicrosoftAccount? Account { get; }
    public string Label { get; }

    public bool IsOffline => Mode == InstanceAccountMode.Offline;
    public bool IsMicrosoft => Mode == InstanceAccountMode.Specific && Account is not null;

    /// <summary>The avatar slot always renders; the Image inside only shows for
    /// rows that have a face (Offline → Steve, MS → player head). Default mode
    /// leaves the slot as an empty dim placeholder so all rows align.</summary>
    public bool HasAvatar => Mode != InstanceAccountMode.Default;

    [ObservableProperty] private Bitmap? _avatar;

    /// <summary>
    /// Kick off the head fetch from Minotar. Idempotent — AvatarService caches
    /// by UUID, so opening the form twice doesn't hit the network twice.
    /// </summary>
    public async Task LoadAvatarAsync(AvatarService avatars, CancellationToken ct = default)
    {
        Bitmap? bitmap = null;
        try
        {
            if (IsOffline)
                bitmap = await avatars.GetSteveHeadAsync(ct).ConfigureAwait(false);
            else if (IsMicrosoft && Account is not null)
                bitmap = await avatars.GetMinecraftHeadAsync(Account.Uuid, ct).ConfigureAwait(false);
            // Default: no avatar, stays null and the empty placeholder box renders.
        }
        catch { /* leave null */ }
        if (bitmap is not null)
            await Dispatcher.UIThread.InvokeAsync(() => Avatar = bitmap);
    }
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
        _accountDisplayName = "";
        // Avatar paints blank until the async fetch lands; RefreshAccountInfoAsync
        // resolves either the player's real head (online) or the Minotar Steve
        // default (offline) and sets AccountAvatar via the dispatcher.
        _ = RefreshAccountInfoAsync();
    }

    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private bool _isConfirmingDelete;
    [ObservableProperty] private bool _isConfiguringJvm;
    [ObservableProperty] private bool _isConfiguringAccount;
    [ObservableProperty] private string _renameInput;

    // Account display row (between modloader/version and 최근 플레이). The bitmap
    // arrives async — null until either Minotar returns the real player head
    // (online) or the Steve default (offline). UI just shows an empty slot in
    // the brief window before the fetch resolves.
    [ObservableProperty] private Bitmap? _accountAvatar;
    [ObservableProperty] private string _accountDisplayName;

    // Inputs for the JVM-override form. Strings so we can distinguish empty
    // ("clear override") from invalid ("ignore"), and so binding survives
    // partial typing.
    [ObservableProperty] private string _minMemoryInput = "";
    [ObservableProperty] private string _maxMemoryInput = "";
    [ObservableProperty] private string _customJvmArgsInput = "";

    // Per-instance account picker (Settings → 인스턴스 → 고급). Replaces the
    // older two-step "mode dropdown → specific account dropdown" with a single
    // flat card list — one row per choice, click to select.
    public ObservableCollection<AccountChoice> AccountChoices { get; } = [];

    [ObservableProperty] private AccountChoice? _selectedAccountChoice;
    [ObservableProperty] private string _offlineUsernameInput = "";

    /// <summary>Snapshot of every signed-in MS account — used by <see cref="RebuildAccountChoices"/>.</summary>
    private IReadOnlyList<MicrosoftAccount> AvailableMicrosoftAccounts =>
        _commands.AvailableMicrosoftAccounts;

    /// <summary>True when the offline username field should be visible.</summary>
    public bool IsSelectedOfflineChoice =>
        SelectedAccountChoice?.IsOffline ?? false;

    partial void OnSelectedAccountChoiceChanged(AccountChoice? value) =>
        OnPropertyChanged(nameof(IsSelectedOfflineChoice));

    /// <summary>
    /// Defensive filter — the <see cref="Controls.UsernameTextBox"/> rejects
    /// invalid keystrokes at the source, but a programmatic set (loading a
    /// stale value from the model written before this validation existed, or
    /// a paste that slips past) can still inject bad chars. Strip them.
    /// </summary>
    partial void OnOfflineUsernameInputChanged(string value)
    {
        var clean = new string(value.Where(MainWindowViewModel.IsValidUsernameChar).Take(16).ToArray());
        if (clean != value) OfflineUsernameInput = clean;
    }

    /// <summary>
    /// Called by the parent VM when its signed-in account list changes. The
    /// account sub-form only listens while it's open — when closed the next
    /// <see cref="BeginConfigureAccount"/> rebuilds the choices from scratch.
    /// </summary>
    public void NotifyAvailableAccountsChanged()
    {
        if (!IsConfiguringAccount) return;

        // Preserve the current selection across the rebuild when possible.
        var preservedMode = SelectedAccountChoice?.Mode ?? InstanceAccountMode.Default;
        var preservedUuid = SelectedAccountChoice?.Account?.Uuid;

        RebuildAccountChoices();
        SelectedAccountChoice = ResolveChoice(preservedMode, preservedUuid?.ToString("N"));
    }

    private void RebuildAccountChoices()
    {
        AccountChoices.Clear();
        AccountChoices.Add(new AccountChoice(
            InstanceAccountMode.Default, null, "활성 계정 사용 (기본)"));
        AccountChoices.Add(new AccountChoice(
            InstanceAccountMode.Offline, null, "오프라인 모드"));
        foreach (var acct in AvailableMicrosoftAccounts)
            AccountChoices.Add(new AccountChoice(
                InstanceAccountMode.Specific, acct, acct.Username));

        // Fire-and-forget avatar loads; each card paints with empty slot then
        // upgrades to the real face as Minotar responds.
        foreach (var choice in AccountChoices)
            _ = choice.LoadAvatarAsync(_commands.AvatarService);
    }

    private AccountChoice ResolveChoice(InstanceAccountMode mode, string? uuidHex)
    {
        return mode switch
        {
            InstanceAccountMode.Offline =>
                AccountChoices.First(c => c.IsOffline),
            InstanceAccountMode.Specific =>
                AccountChoices.FirstOrDefault(c => c.IsMicrosoft
                    && string.Equals(c.Account!.Uuid.ToString("N"), uuidHex, StringComparison.OrdinalIgnoreCase))
                ?? AccountChoices.First(c => c.Mode == InstanceAccountMode.Default),
            _ => AccountChoices.First(c => c.Mode == InstanceAccountMode.Default),
        };
    }

    /// <summary>
    /// True when the card shows the normal info+actions grid. Renaming happens
    /// inline on the name TextBlock (#108), so it intentionally does NOT hide
    /// the default grid the way delete-confirm / JVM-config / account-config do.
    /// </summary>
    public bool IsDefaultMode =>
        !IsConfirmingDelete && !IsConfiguringJvm && !IsConfiguringAccount;

    partial void OnIsConfirmingDeleteChanged(bool value)   => OnPropertyChanged(nameof(IsDefaultMode));
    partial void OnIsConfiguringJvmChanged(bool value)     => OnPropertyChanged(nameof(IsDefaultMode));
    partial void OnIsConfiguringAccountChanged(bool value) => OnPropertyChanged(nameof(IsDefaultMode));

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

    /// <summary>
    /// Re-resolve which account would launch this instance and update the
    /// avatar + display name. Called whenever:
    ///   - the card's model changes (UpdateModel below)
    ///   - the parent VM's signed-in account list changes (sign-out, sign-in)
    ///   - the active account pointer changes (Default-mode instances follow it)
    /// Fire-and-forget on the caller side; bitmap arrives via property change.
    /// </summary>
    public async Task RefreshAccountInfoAsync(CancellationToken ct = default)
    {
        InstanceAvatarInfo info;
        try { info = _commands.ResolveAvatarInfo(Model); }
        catch { return; }

        Bitmap? bitmap;
        try
        {
            bitmap = info.MinecraftUuid is Guid uuid
                ? await _commands.AvatarService.GetMinecraftHeadAsync(uuid, ct).ConfigureAwait(false)
                : await _commands.AvatarService.GetSteveHeadAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            bitmap = null;
        }

        // ConfigureAwait(false) above means we're on the thread pool here;
        // marshal the property changes back to the UI thread so the binding
        // system sees them on the right context.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            AccountDisplayName = info.DisplayName;
            AccountAvatar = bitmap;
        });
    }

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
        else
        {
            // No-op rename (blank input or unchanged) — re-sync the input back
            // to the current model name so the next BeginRename starts clean
            // instead of remembering the abandoned blank/whitespace value.
            RenameInput = Model.Name;
        }
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
        IsConfiguringAccount = false;
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

    // ---- Account picker sub-form ----------------------------------------
    //
    // Opened by clicking the small pencil icon next to the avatar+username
    // row on the default card. Uses the same AccountChoices ListBox as before
    // (just lifted out of the JVM form so casual users see "account" as its
    // own action, not buried inside JVM tweaks).

    [RelayCommand]
    private void BeginConfigureAccount()
    {
        IsRenaming = false;
        IsConfirmingDelete = false;
        IsConfiguringJvm = false;

        RebuildAccountChoices();
        SelectedAccountChoice = ResolveChoice(Model.AccountMode, Model.SpecificAccountId);
        OfflineUsernameInput = Model.OfflineUsername ?? "";

        IsConfiguringAccount = true;
    }

    [RelayCommand]
    private void CancelAccountConfig() => IsConfiguringAccount = false;

    [RelayCommand]
    private void SaveAccountConfig()
    {
        var choice = SelectedAccountChoice;
        var mode = choice?.Mode ?? InstanceAccountMode.Default;
        var specificId = mode == InstanceAccountMode.Specific
            ? choice?.Account?.Uuid.ToString("N")
            : null;
        var offlineName = mode == InstanceAccountMode.Offline
            ? (string.IsNullOrWhiteSpace(OfflineUsernameInput) ? null : OfflineUsernameInput.Trim())
            : null;
        _commands.UpdateAccountSettings(Model, mode, specificId, offlineName);
        IsConfiguringAccount = false;
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
