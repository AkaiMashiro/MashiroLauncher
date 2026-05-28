using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MashiroLauncher.App.Services;
using MashiroLauncher.Core.Auth;
using MashiroLauncher.Core.Auth.Microsoft;
using MashiroLauncher.Core.Bedrock;
using MashiroLauncher.Core.Common;
using MashiroLauncher.Core.Instances;
using MashiroLauncher.Core.Launching;
using MashiroLauncher.Core.Modloaders.Modrinth;
using MashiroLauncher.Core.Modloaders.Mrpack;
using MashiroLauncher.Core.Versions.Mojang;

namespace MashiroLauncher.App.ViewModels;

public enum LaunchState { Idle, Preparing, Running }
public enum StatusKind { None, Info, Success, Error }
public enum AppView { Welcome, AccountSelection, JavaPlay, BedrockPlay }
public enum SettingsCategory { General, Appearance, Mods, Instances, Advanced, Developer, Updates, About }

public partial class MainWindowViewModel : ObservableObject, IInstanceCommands, IModCommands, IModDetailCommands
{
    private readonly Downloader _downloader;
    private readonly HttpClient _http;
    private readonly MojangStatusService _statusService;
    private readonly BackgroundService _backgroundService;
    private readonly UiLaunchService _launchService;
    private readonly MicrosoftAuthService _msAuth;
    private readonly UpdateService _updateService;
    private readonly ModrinthClient _modrinth;
    private readonly ModrinthInstaller _modInstaller;
    private readonly InstanceStorage _instanceStorage = new();
    private readonly Func<Task<string?>> _pickImageAsync;
    private readonly Func<Task<IReadOnlyList<string>?>> _pickModFilesAsync;
    private readonly Func<Task<string?>> _pickMrpackAsync;
    private readonly Func<string, Task<string?>> _pickInstanceZipSaveAsync;
    private readonly Func<Task<string?>> _pickInstanceZipOpenAsync;

    public MainWindowViewModel(
        Downloader downloader,
        HttpClient http,
        MojangStatusService statusService,
        BackgroundService backgroundService,
        UiLaunchService launchService,
        MicrosoftAuthService msAuth,
        UpdateService updateService,
        Func<Task<string?>> pickImageAsync,
        Func<Task<IReadOnlyList<string>?>> pickModFilesAsync,
        Func<Task<string?>> pickMrpackAsync,
        Func<string, Task<string?>> pickInstanceZipSaveAsync,
        Func<Task<string?>> pickInstanceZipOpenAsync)
    {
        _downloader = downloader;
        _http = http;
        _statusService = statusService;
        _backgroundService = backgroundService;
        _launchService = launchService;
        _msAuth = msAuth;
        _updateService = updateService;
        _modrinth = new ModrinthClient(http);
        _modInstaller = new ModrinthInstaller(http);
        _pickImageAsync = pickImageAsync;
        _pickModFilesAsync = pickModFilesAsync;
        _pickMrpackAsync = pickMrpackAsync;
        _pickInstanceZipSaveAsync = pickInstanceZipSaveAsync;
        _pickInstanceZipOpenAsync = pickInstanceZipOpenAsync;
        BackgroundImage = _backgroundService.LoadCurrent();

        // New build available → surface as a toast. Suppress while the game
        // is actually running so we don't pop UI under the player.
        _updateService.UpdateAvailable += (_, info) =>
            Dispatcher.UIThread.Post(() =>
            {
                // Don't pop the update toast while any launch is in-flight — it
                // would land under the player. They'll see it next time they're
                // back at idle.
                if (HasActiveLaunch) return;
                PendingUpdate = info;
            });

        var settings = new SettingsStorage().Load();
        MinMemoryMbValue = settings.MinMemoryMb;
        MaxMemoryMbValue = settings.MaxMemoryMb;
        CustomJvmArgs = settings.CustomJvmArgs;
        _isInstanceModeEnabled = settings.UseInstanceMode;  // backing field set so OnChanged doesn't fire during init

        // Default the sort picker to 다운로드순. Set via backing field so the
        // OnChanged handler doesn't fire a no-op search before SelectedModInstance
        // is even picked.
        _selectedModSort = ModSortOptions[0];

        // Empty-state visibility + moddable-instance filter both depend on the instance list.
        Instances.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoInstances));
            OnPropertyChanged(nameof(ModdableInstances));
            OnPropertyChanged(nameof(HasNoModdableInstance));
        };

        // Refreshing the installed-mods list whenever a mod operation touches
        // the collection keeps the UI consistent without manual refresh calls.
        InstalledMods.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoInstalledMods));
            OnPropertyChanged(nameof(InstalledModsCountLabel));
            OnPropertyChanged(nameof(ShowEmptyInstalledHint));
        };

        // PLAY button label + progress visibility both depend on the count.
        RunningLaunches.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(LaunchButtonText));
            OnPropertyChanged(nameof(HasActiveLaunch));
        };
    }

    public ObservableCollection<string> AvailableVersions { get; } = [];
    public ObservableCollection<InstanceItemViewModel> Instances { get; } = [];

    public bool IsCategoryGeneral    => SettingsCategory == SettingsCategory.General;
    public bool IsCategoryAppearance => SettingsCategory == SettingsCategory.Appearance;
    public bool IsCategoryMods       => SettingsCategory == SettingsCategory.Mods;
    public bool IsCategoryInstances  => SettingsCategory == SettingsCategory.Instances;
    public bool IsCategoryAdvanced   => SettingsCategory == SettingsCategory.Advanced;
    public bool IsCategoryDeveloper  => SettingsCategory == SettingsCategory.Developer;
    public bool IsCategoryUpdates    => SettingsCategory == SettingsCategory.Updates;
    public bool IsCategoryAbout      => SettingsCategory == SettingsCategory.About;

    partial void OnSettingsCategoryChanged(SettingsCategory value)
    {
        OnPropertyChanged(nameof(IsCategoryGeneral));
        OnPropertyChanged(nameof(IsCategoryAppearance));
        OnPropertyChanged(nameof(IsCategoryMods));
        OnPropertyChanged(nameof(IsCategoryInstances));
        OnPropertyChanged(nameof(IsCategoryAdvanced));
        OnPropertyChanged(nameof(IsCategoryDeveloper));
        OnPropertyChanged(nameof(IsCategoryUpdates));
        OnPropertyChanged(nameof(IsCategoryAbout));

        // Lazy-load the log viewer file list when the user first lands on the
        // 개발자 tab. Cheap enough to re-scan each visit (handful of files).
        if (value == SettingsCategory.Developer)
            RefreshLogListCommand.Execute(null);
    }

    [RelayCommand]
    private void SelectSettingsCategory(string? name)
    {
        if (Enum.TryParse<SettingsCategory>(name, out var cat))
            SettingsCategory = cat;
    }

    [RelayCommand]
    private void SaveAdvancedSettings()
    {
        try
        {
            // Sanity check: max should be >= min
            if (MaxMemoryMbValue < MinMemoryMbValue)
                MaxMemoryMbValue = MinMemoryMbValue;
            var settings = new LauncherSettings(
                (int)MinMemoryMbValue,
                (int)MaxMemoryMbValue,
                CustomJvmArgs ?? "",
                IsInstanceModeEnabled);
            new SettingsStorage().Save(settings);
            SetStatus("설정이 저장되었습니다", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"저장 실패: {ex.Message}", StatusKind.Error);
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(Paths.Logs);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Paths.Logs,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus($"폴더 열기 실패: {ex.Message}", StatusKind.Error);
        }
    }

    // ---- In-app log viewer (Settings → 개발자) ----------------------------
    //
    // Two log sources surfaced side-by-side in the ComboBox:
    //   - 런처 · <name>.log         from data/logs/*.log
    //   - 인스턴스 · <name> · latest.log
    //                              from data/instances/<id>/game/logs/latest.log
    //                              (the file Minecraft itself writes — chat,
    //                              kicks, server connects, mod load lines)

    /// <summary>One entry in the log-file picker. <see cref="DisplayLabel"/> is
    /// what shows in the ComboBox; <see cref="FilePath"/> is the absolute path
    /// we read from. <see cref="SortKey"/> keeps launcher logs above instance
    /// logs without depending on alphabetical ordering of Korean prefixes.</summary>
    public sealed record LogFileEntry(string DisplayLabel, string FilePath, int SortKey);

    public ObservableCollection<LogFileEntry> AvailableLogFiles { get; } = [];

    [ObservableProperty] private LogFileEntry? _selectedLogFile;
    [ObservableProperty] private string _logContent = "";

    /// <summary>Cap the inline log viewer so a 200MB latest.log doesn't lock up the UI.</summary>
    private const int MaxLogTailLines = 500;

    /// <summary>Re-scan launcher + per-instance log files and (re)load the current selection's tail.</summary>
    [RelayCommand]
    private void RefreshLogList()
    {
        var prevPath = SelectedLogFile?.FilePath;
        AvailableLogFiles.Clear();

        // 1) Launcher-side logs (mods.log, launch.log, minecraft.log, …)
        try
        {
            if (Directory.Exists(Paths.Logs))
            {
                foreach (var path in Directory.GetFiles(Paths.Logs, "*.log")
                             .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(path);
                    AvailableLogFiles.Add(new LogFileEntry($"런처 · {name}", path, SortKey: 0));
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus($"런처 로그 목록 실패: {ex.Message}", StatusKind.Error);
        }

        // 2) Each instance's Minecraft-managed latest.log (only if it exists —
        //    instances that haven't been launched yet won't have one).
        foreach (var card in Instances.OrderBy(c => c.Model.Name, StringComparer.OrdinalIgnoreCase))
        {
            var latest = Path.Combine(Paths.InstanceGameDir(card.Model.Id), "logs", "latest.log");
            if (File.Exists(latest))
            {
                AvailableLogFiles.Add(new LogFileEntry(
                    $"인스턴스 · {card.Model.Name} · latest.log", latest, SortKey: 1));
            }
        }

        // Re-select previous file when its path still exists; otherwise pick the first.
        SelectedLogFile = prevPath is not null
            ? AvailableLogFiles.FirstOrDefault(e => string.Equals(e.FilePath, prevPath, StringComparison.OrdinalIgnoreCase))
              ?? AvailableLogFiles.FirstOrDefault()
            : AvailableLogFiles.FirstOrDefault();

        // SelectedLogFile setter triggers LoadLogContent only if the value
        // actually changes; force a reload so 새로고침 always re-reads from disk.
        LoadLogContent();
    }

    partial void OnSelectedLogFileChanged(LogFileEntry? value) => LoadLogContent();

    private void LoadLogContent()
    {
        if (SelectedLogFile is null)
        {
            LogContent = "";
            return;
        }
        var path = SelectedLogFile.FilePath;
        if (!File.Exists(path))
        {
            LogContent = "(파일이 존재하지 않습니다)";
            return;
        }
        try
        {
            // Read with FileShare.ReadWrite so we can peek at files Minecraft / the
            // installer is currently writing without blocking them.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var allLines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
                allLines.Add(line);

            // Tail the last N lines so giant logs stay snappy.
            var tail = allLines.Count > MaxLogTailLines
                ? allLines.Skip(allLines.Count - MaxLogTailLines).ToList()
                : allLines;
            var truncatedNotice = allLines.Count > MaxLogTailLines
                ? $"(앞부분 {allLines.Count - MaxLogTailLines}줄 생략 — 마지막 {MaxLogTailLines}줄만 표시)\n\n"
                : "";
            LogContent = truncatedNotice + string.Join("\n", tail);
        }
        catch (Exception ex)
        {
            LogContent = $"(로그를 읽을 수 없습니다: {ex.Message})";
        }
    }

    [ObservableProperty] private Bitmap? _backgroundImage;
    [ObservableProperty] private string _username = "Player";
    [ObservableProperty] private MojangStatus _mojangStatus = MojangStatus.Unknown;
    [ObservableProperty] private LaunchState _launchPhase = LaunchState.Idle;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private StatusKind _statusKind = StatusKind.None;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private AppView _currentView = AppView.Welcome;
    [ObservableProperty] private MicrosoftAccount? _signedInAccount;
    [ObservableProperty] private bool _isAuthenticating;
    [ObservableProperty] private BedrockInstallInfo? _bedrockInfo;
    [ObservableProperty] private bool _isBedrockInstalling;

    [ObservableProperty] private SettingsCategory _settingsCategory = SettingsCategory.General;
    [ObservableProperty] private decimal _minMemoryMbValue = 512m;
    [ObservableProperty] private decimal _maxMemoryMbValue = 4096m;
    [ObservableProperty] private string _customJvmArgs = "";

    // Play view — two interchangeable modes, controlled by IsInstanceModeEnabled
    // (persisted launcher setting, toggle lives in Settings → 일반):
    //   - false (default): quick mode using SelectedVersion + UseFabric
    //   - true:             pick a named instance from Instances directly on the Play view
    [ObservableProperty] private string? _selectedVersion;
    [ObservableProperty] private bool _useFabric;
    [ObservableProperty] private bool _isInstanceModeEnabled;
    [ObservableProperty] private InstanceItemViewModel? _selectedPlayInstance;

    partial void OnIsInstanceModeEnabledChanged(bool value)
    {
        PersistLauncherSettings();
        LaunchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanLaunch));
    }

    partial void OnSelectedPlayInstanceChanged(InstanceItemViewModel? value)
    {
        LaunchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanLaunch));
    }

    private void PersistLauncherSettings()
    {
        try
        {
            new SettingsStorage().Save(new LauncherSettings(
                (int)MinMemoryMbValue,
                (int)MaxMemoryMbValue,
                CustomJvmArgs ?? "",
                IsInstanceModeEnabled));
        }
        catch { /* best-effort */ }
    }

    // Microsoft sign-in is now a single round-trip via WebView2 — no UI state
    // needs to live on this VM. See LoginMicrosoftAsync below.

    // ---- Mods (Modrinth) ---------------------------------------------------

    public ObservableCollection<ModItemViewModel> ModSearchResults { get; } = [];
    public ObservableCollection<InstalledModViewModel> InstalledMods { get; } = [];

    /// <summary>Subset of Instances that can load mods (Fabric or NeoForge).</summary>
    public IEnumerable<InstanceItemViewModel> ModdableInstances =>
        Instances.Where(vm => vm.Model.Modloader is Modloader.Fabric or Modloader.NeoForge);

    public bool HasNoModdableInstance => !ModdableInstances.Any();
    public bool HasSelectedModInstance => SelectedModInstance is not null;
    public bool HasNoInstalledMods =>
        SelectedModInstance is not null && InstalledMods.Count == 0;

    [ObservableProperty] private InstanceItemViewModel? _selectedModInstance;
    [ObservableProperty] private string _modSearchQuery = "";
    [ObservableProperty] private bool _isModSearching;
    [ObservableProperty] private string _modStatusText = "";

    // Pagination — Modrinth returns up to 100 per call but 20 keeps each
    // request fast and predictable. Each "더 보기" click advances offset by
    // this much, so the user can choose to see more without us preloading
    // everything up front.
    private const int ModSearchPageSize = 20;
    [ObservableProperty] private bool _isLoadingMoreMods;
    private int _currentSearchOffset;
    private int _lastSearchTotalHits;

    // Sort picker — pairs a Korean label for the ComboBox with the API value
    // we forward to Modrinth's ?index= parameter. Defaults to "다운로드순" so
    // the empty-query case still surfaces top mods.
    public sealed record ModSortOption(string Label, string ApiValue);

    public ObservableCollection<ModSortOption> ModSortOptions { get; } =
    [
        new("다운로드순", "downloads"),
        new("관련도순", "relevance"),
        new("최신순", "newest"),
        new("업데이트순", "updated"),
        new("팔로워순", "follows"),
    ];

    [ObservableProperty] private ModSortOption? _selectedModSort;

    partial void OnSelectedModSortChanged(ModSortOption? value)
    {
        // Skip the initial set + ignore when no instance is picked yet — the
        // search worker bails out itself, but this also avoids a useless API
        // call on first construction.
        if (value is null || SelectedModInstance is null) return;
        _ = SearchModsAsync();
    }

    /// <summary>True when there are more results on the server we haven't pulled yet.</summary>
    public bool CanLoadMoreMods =>
        !IsModSearching
        && !IsLoadingMoreMods
        && ModSearchResults.Count > 0
        && ModSearchResults.Count < _lastSearchTotalHits;

    partial void OnIsModSearchingChanged(bool value) => OnPropertyChanged(nameof(CanLoadMoreMods));
    partial void OnIsLoadingMoreModsChanged(bool value) => OnPropertyChanged(nameof(CanLoadMoreMods));

    // Installed mods section is collapsible — defaults to collapsed so a large
    // mod list doesn't push the search results way down the page.
    [ObservableProperty] private bool _isInstalledModsExpanded;

    public string InstalledModsCountLabel =>
        InstalledMods.Count == 0 ? "" : $"({InstalledMods.Count})";

    /// <summary>Empty-state hint only shown when the section is expanded AND there's nothing in it.</summary>
    public bool ShowEmptyInstalledHint => HasNoInstalledMods && IsInstalledModsExpanded;

    partial void OnIsInstalledModsExpandedChanged(bool value) =>
        OnPropertyChanged(nameof(ShowEmptyInstalledHint));

    [RelayCommand]
    private void ToggleInstalledMods() => IsInstalledModsExpanded = !IsInstalledModsExpanded;

    /// <summary>
    /// User clicks "외부 jar 가져오기" — pop a multi-select file picker, copy any
    /// chosen .jars into the instance's mods/ folder, then refresh the
    /// installed-mods list. Files that already exist by name are skipped (we
    /// don't want to silently clobber a tweaked override of the same jar).
    /// They show up in the installed list as "external" cards (no Modrinth
    /// metadata, "?" placeholder icon) because they're absent from .modrinth.json.
    /// </summary>
    [RelayCommand]
    private async Task ImportExternalModsAsync()
    {
        if (SelectedModInstance is null)
        {
            SetStatus("먼저 모드 인스턴스를 선택해 주세요", StatusKind.Info);
            return;
        }

        IReadOnlyList<string>? files;
        try { files = await _pickModFilesAsync(); }
        catch (Exception ex)
        {
            SetStatus($"파일 탐색기 열기 실패: {ex.Message}", StatusKind.Error);
            LogException("mods.log", ex);
            return;
        }
        if (files is null || files.Count == 0) return;

        var modsDir = Path.Combine(Paths.InstanceGameDir(SelectedModInstance.Model.Id), "mods");
        Directory.CreateDirectory(modsDir);

        int imported = 0, skipped = 0, failed = 0;
        foreach (var src in files)
        {
            var name = Path.GetFileName(src);
            // Defense in depth: the file picker filter already restricts to .jar,
            // but a user typing in the path field could bypass that.
            if (string.IsNullOrEmpty(name) ||
                !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }
            var dst = Path.Combine(modsDir, name);
            if (File.Exists(dst))
            {
                skipped++;
                continue;
            }
            try
            {
                File.Copy(src, dst, overwrite: false);
                imported++;
            }
            catch (Exception ex)
            {
                failed++;
                LogException("mods.log", ex);
            }
        }

        RefreshInstalledMods();
        // Auto-expand the list so the user sees the new entries immediately.
        if (imported > 0) IsInstalledModsExpanded = true;

        var summary = (imported, skipped, failed) switch
        {
            (0, 0, 0)            => ("가져온 모드가 없습니다", StatusKind.Info),
            (> 0, 0, 0)          => ($"{imported}개 모드를 가져왔습니다", StatusKind.Success),
            (> 0, > 0, 0)        => ($"{imported}개 가져옴 · {skipped}개는 이미 있어서 건너뜀", StatusKind.Info),
            (_, _, > 0)          => ($"{imported}개 성공 · {failed}개 실패 (mods.log 참고)", StatusKind.Error),
            _                    => ($"{imported}개 가져옴, {skipped}개 건너뜀", StatusKind.Info),
        };
        SetStatus(summary.Item1, summary.Item2);
    }

    // Debouncer for live search — restart the 400ms timer on every keystroke
    // and only actually hit Modrinth after the user pauses typing.
    private CancellationTokenSource? _modSearchDebounceCts;

    partial void OnSelectedModInstanceChanged(InstanceItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedModInstance));
        ModSearchResults.Clear();
        ModStatusText = "";
        RefreshInstalledMods();
        // Show popular mods immediately when an instance is picked — empty
        // query + index=downloads ranks by total downloads.
        if (value is not null)
            _ = SearchModsAsync();
    }

    partial void OnModSearchQueryChanged(string value) => DebouncedSearch();

    private void DebouncedSearch()
    {
        _modSearchDebounceCts?.Cancel();
        if (SelectedModInstance is null) return;

        var cts = new CancellationTokenSource();
        _modSearchDebounceCts = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(400, cts.Token); }
            catch (OperationCanceledException) { return; }
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (cts.IsCancellationRequested) return;
                await SearchModsAsync();
            });
        });
    }

    private void RefreshInstalledMods()
    {
        InstalledMods.Clear();
        if (SelectedModInstance is null) return;

        var instanceId = SelectedModInstance.Model.Id;
        var entries = _modInstaller.ListInstalled(instanceId);
        var filenameToProject = _modInstaller.GetFilenameToProjectIdMap(instanceId);

        foreach (var entry in entries)
        {
            filenameToProject.TryGetValue(entry.Filename, out var projectId);
            var card = new InstalledModViewModel(entry.Filename, projectId, entry.IsEnabled, this);
            InstalledMods.Add(card);
            // For tracked mods, fetch the friendly title + icon in the
            // background so the card upgrades from filename → real metadata.
            if (projectId is not null)
                _ = card.LoadMetadataAsync(_modrinth, _http);
        }

        // Re-mark each visible search result as 설치됨 if its project id is now
        // (or no longer) in the manifest.
        var installedIds = _modInstaller.GetInstalledProjectIds(instanceId);
        foreach (var card in ModSearchResults)
            card.IsInstalled = installedIds.Contains(card.Hit.ProjectId);

        OnPropertyChanged(nameof(HasNoInstalledMods));
    }

    [RelayCommand]
    private Task SearchModsAsync() => RunSearchAsync(offset: 0, append: false);

    [RelayCommand]
    private Task LoadMoreModsAsync() => RunSearchAsync(_currentSearchOffset + ModSearchPageSize, append: true);

    /// <summary>
    /// Single search worker shared by the initial query path and the "더 보기"
    /// pagination path. When <paramref name="append"/> is false we clear the
    /// list and flip <see cref="IsModSearching"/>; when true we keep existing
    /// cards and use <see cref="IsLoadingMoreMods"/> instead so the spinner
    /// shows in the right place and the result list doesn't flicker.
    /// </summary>
    private async Task RunSearchAsync(int offset, bool append)
    {
        if (SelectedModInstance is null) return;
        if (append) IsLoadingMoreMods = true;
        else
        {
            IsModSearching = true;
            ModSearchResults.Clear();
            _currentSearchOffset = 0;
            _lastSearchTotalHits = 0;
            ModStatusText = "검색 중…";
        }

        try
        {
            var instance = SelectedModInstance.Model;
            var loader = instance.Modloader.ToString().ToLowerInvariant(); // "fabric"
            var sortIndex = SelectedModSort?.ApiValue ?? "downloads";
            var result = await _modrinth.SearchAsync(
                ModSearchQuery, instance.VersionId, loader,
                limit: ModSearchPageSize, offset: offset, sortIndex: sortIndex,
                ct: CancellationToken.None);

            // Pre-compute the installed-project set once per search so each
            // card lookup is O(1).
            var installedIds = _modInstaller.GetInstalledProjectIds(instance.Id);
            foreach (var hit in result.Hits)
            {
                // Belt-and-suspenders: the facet filter on the server side
                // should already ensure this, but if a hit somehow comes back
                // missing our loader in its `categories`, mark it incompatible
                // so the UI doesn't offer an install button that would 500.
                // Missing categories array → assume compatible (data we don't
                // have shouldn't disable the card).
                var isCompat = hit.Categories is null
                    || hit.Categories.Any(c => string.Equals(c, loader, StringComparison.OrdinalIgnoreCase));
                var vm = new ModItemViewModel(hit, isCompat, this)
                {
                    IsInstalled = installedIds.Contains(hit.ProjectId),
                };
                ModSearchResults.Add(vm);
                _ = vm.LoadIconAsync(_http);  // fire-and-forget; icons fade in
            }

            _currentSearchOffset = offset;
            _lastSearchTotalHits = result.TotalHits;

            ModStatusText = ModSearchResults.Count == 0
                ? "결과 없음"
                : $"{ModSearchResults.Count}개 표시 / 총 {result.TotalHits}개";
        }
        catch (Exception ex)
        {
            // Surface as toast (consistent with other failure paths) and keep
            // the inline hint clean.
            ModStatusText = "";
            SetStatus($"모드 검색 실패: {ex.Message}", StatusKind.Error);
            LogException("mods.log", ex);
        }
        finally
        {
            if (append) IsLoadingMoreMods = false;
            else IsModSearching = false;
            OnPropertyChanged(nameof(CanLoadMoreMods));
        }
    }

    [RelayCommand]
    private void UninstallMod(string? filename) => DoUninstallMod(filename);

    private void DoUninstallMod(string? filename)
    {
        if (SelectedModInstance is null || string.IsNullOrEmpty(filename)) return;
        try
        {
            _modInstaller.Uninstall(SelectedModInstance.Model.Id, filename);
            RefreshInstalledMods();
            SetStatus($"제거됨: {filename}", StatusKind.Info);
        }
        catch (Exception ex)
        {
            SetStatus($"제거 실패: {ex.Message}", StatusKind.Error);
        }
    }

    // IModCommands.Uninstall — called by individual InstalledModViewModel cards.
    void IModCommands.Uninstall(string filename) => DoUninstallMod(filename);

    // ---- Mod update detection --------------------------------------------

    [ObservableProperty] private bool _isCheckingUpdates;
    [ObservableProperty] private int _updatesAvailableCount;
    [ObservableProperty] private bool _showAllModsUpToDate;

    private CancellationTokenSource? _allUpToDateDismissCts;

    public bool HasModUpdates => UpdatesAvailableCount > 0;

    /// <summary>
    /// "업데이트 확인" button is hidden only when results say "N개 업데이트
    /// 가능" — that case shows the green action chip instead. Checking and
    /// "done" states render inline within this button so the widget itself
    /// stays a stable ghost button (no chip border, no size jump).
    /// </summary>
    public bool ShowCheckUpdatesButton => !HasModUpdates;

    partial void OnUpdatesAvailableCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasModUpdates));
        OnPropertyChanged(nameof(UpdatesAvailableLabel));
        OnPropertyChanged(nameof(ShowCheckUpdatesButton));
    }

    public string UpdatesAvailableLabel => UpdatesAvailableCount switch
    {
        0 => "",
        _ => $"{UpdatesAvailableCount}개 업데이트 가능",
    };

    /// <summary>Background scan: ask Modrinth whether any tracked mod has a
    /// newer compatible version. Marks cards with AvailableUpdate so the UI
    /// can render the "업데이트" button. Inline chip handles its own progress +
    /// "all up to date" feedback — no toast (which is hidden by the settings
    /// overlay anyway).</summary>
    [RelayCommand]
    private async Task CheckModUpdatesAsync()
    {
        if (SelectedModInstance is null || IsCheckingUpdates) return;
        IsCheckingUpdates = true;
        // Clear any previous "모두 최신" chip so the spinner is the only state visible.
        _allUpToDateDismissCts?.Cancel();
        ShowAllModsUpToDate = false;
        try
        {
            var instance = SelectedModInstance.Model;
            var loader = instance.Modloader.ToString().ToLowerInvariant();
            var updates = await _modInstaller.CheckUpdatesAsync(
                instance.Id, instance.VersionId, loader, CancellationToken.None);

            // Apply results on the UI thread (CheckUpdatesAsync hops threads).
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var card in InstalledMods)
                {
                    card.AvailableUpdate = card.ProjectId is not null
                        && updates.TryGetValue(card.ProjectId, out var v)
                        ? v
                        : null;
                }
                UpdatesAvailableCount = updates.Count;
                if (updates.Count > 0) IsInstalledModsExpanded = true;
            });

            if (updates.Count == 0)
            {
                // Show the "✓ 모두 최신" chip in the same slot as the button for
                // 3 seconds, then return the slot to the idle button.
                ShowAllModsUpToDate = true;
                ScheduleAllUpToDateDismiss();
            }
        }
        catch (Exception ex)
        {
            // Errors keep toasting — they're rare and worth being loud about.
            SetStatus($"업데이트 확인 실패: {ex.Message}", StatusKind.Error);
            LogException("mods.log", ex);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    private void ScheduleAllUpToDateDismiss()
    {
        _allUpToDateDismissCts?.Cancel();
        var cts = new CancellationTokenSource();
        _allUpToDateDismissCts = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), cts.Token); }
            catch (OperationCanceledException) { return; }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!cts.IsCancellationRequested) ShowAllModsUpToDate = false;
            });
        });
    }

    /// <summary>Run UpdateMod on every installed mod card that currently has
    /// an available update. Sequential so progress/status messages don't trip
    /// over each other.</summary>
    [RelayCommand]
    private async Task UpdateAllModsAsync()
    {
        var pending = InstalledMods.Where(m => m.HasUpdate).ToList();
        if (pending.Count == 0) return;
        var initialCount = pending.Count;
        var succeeded = 0;
        foreach (var mod in pending)
        {
            if (!mod.HasUpdate) continue;
            try
            {
                await mod.UpdateCommand.ExecuteAsync(null);
                succeeded++;
            }
            catch { /* per-mod failures already surfaced as status toasts */ }
        }
        UpdatesAvailableCount = InstalledMods.Count(m => m.HasUpdate);
        SetStatus(
            succeeded == initialCount
                ? $"{succeeded}개 모드 업데이트 완료"
                : $"{succeeded}/{initialCount}개 모드 업데이트 (일부 실패)",
            succeeded == initialCount ? StatusKind.Success : StatusKind.Info);
    }

    /// <summary>
    /// Update a single mod to its <see cref="InstalledModViewModel.AvailableUpdate"/>.
    /// We install the new version (which overwrites the manifest entry) and
    /// then delete the orphaned old jar if its filename changed. The disabled
    /// state of the old jar is preserved on the new one.
    /// </summary>
    async Task IModCommands.UpdateModAsync(InstalledModViewModel mod)
    {
        if (SelectedModInstance is null || mod.ProjectId is null || mod.AvailableUpdate is null) return;
        var instance = SelectedModInstance.Model;
        var loader = instance.Modloader.ToString().ToLowerInvariant();
        var newVersion = mod.AvailableUpdate;
        var oldFilename = mod.Filename;
        var wasDisabled = oldFilename.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
        var oldEnabledName = wasDisabled ? oldFilename[..^".disabled".Length] : oldFilename;

        try
        {
            var progress = new Progress<ModInstallProgress>(p => ModStatusText = p.Stage);
            await _modInstaller.InstallVersionAsync(
                mod.ProjectId, newVersion.Id,
                instance.Id, instance.VersionId, loader, progress, CancellationToken.None);

            // The installer wrote the new file (with possibly a new name) and
            // updated the manifest. Find that new filename to handle orphans
            // + preserve the disabled state.
            var modsDir = Path.Combine(Paths.InstanceGameDir(instance.Id), "mods");
            var newEnabledName = _modInstaller.GetFilenameToProjectIdMap(instance.Id)
                .Where(kv => string.Equals(kv.Value, mod.ProjectId, StringComparison.OrdinalIgnoreCase))
                .Select(kv =>
                {
                    // Normalize: the map returns whichever form (.jar or .jar.disabled) is on disk.
                    var k = kv.Key;
                    return k.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
                        ? k[..^".disabled".Length]
                        : k;
                })
                .FirstOrDefault();

            if (newEnabledName is not null
                && !string.Equals(newEnabledName, oldEnabledName, StringComparison.OrdinalIgnoreCase))
            {
                // Orphaned old jar — manifest already points to the new file, so
                // we can safely delete the old one (either form).
                foreach (var candidate in new[] { oldEnabledName, oldEnabledName + ".disabled" })
                {
                    var oldPath = Path.Combine(modsDir, candidate);
                    try { if (File.Exists(oldPath)) File.Delete(oldPath); }
                    catch { /* best-effort cleanup */ }
                }
            }

            // Restore disabled state on the new file if the old was disabled.
            if (wasDisabled && newEnabledName is not null)
            {
                try { _modInstaller.SetEnabled(instance.Id, newEnabledName, enabled: false); }
                catch { /* state preservation is best-effort */ }
            }

            RefreshInstalledMods();
            // After refresh the new card instance replaced `mod`, so the toast
            // uses the cached title before refresh wipes the old card.
            SetStatus(
                $"'{mod.Title}' {newVersion.VersionNumber}(으)로 업데이트됨",
                StatusKind.Success);
            ModStatusText = "";
            UpdatesAvailableCount = InstalledMods.Count(m => m.HasUpdate);
        }
        catch (Exception ex)
        {
            SetStatus($"업데이트 실패: {ex.Message}", StatusKind.Error);
            ModStatusText = "";
            LogException("mods.log", ex);
        }
    }

    // IModCommands.ToggleEnabled — InstalledModViewModel flipped its switch.
    void IModCommands.ToggleEnabled(InstalledModViewModel mod, bool enable)
    {
        if (SelectedModInstance is null) return;
        try
        {
            var newName = _modInstaller.SetEnabled(SelectedModInstance.Model.Id, mod.Filename, enable);
            if (newName != mod.Filename)
                mod.ApplyRename(newName);
            // Status feedback is informational — the card visual already updated.
            SetStatus(
                enable ? $"'{mod.Title}' 활성화됨" : $"'{mod.Title}' 비활성화됨",
                StatusKind.Info);
        }
        catch (Exception ex)
        {
            // Rename failed (locked file? perms?) — roll back the visual toggle
            // so the card reflects on-disk truth.
            mod.ApplyEnabledState(!enable);
            SetStatus($"모드 상태 변경 실패: {ex.Message}", StatusKind.Error);
            LogException("mods.log", ex);
        }
    }

    // IModCommands.InstallAsync — called by individual ModItemViewModel cards.
    async Task IModCommands.InstallAsync(ModrinthSearchHit hit)
    {
        if (SelectedModInstance is null) return;
        var instance = SelectedModInstance.Model;
        var loader = instance.Modloader.ToString().ToLowerInvariant();
        try
        {
            var progress = new Progress<ModInstallProgress>(p =>
                ModStatusText = p.Stage);
            await _modInstaller.InstallAsync(
                hit.ProjectId, instance.Id, instance.VersionId, loader, progress, CancellationToken.None);
            RefreshInstalledMods();
            SetStatus($"'{hit.Title}' 설치됨", StatusKind.Success);
            ModStatusText = "";
        }
        catch (Exception ex)
        {
            SetStatus($"설치 실패: {ex.Message}", StatusKind.Error);
            ModStatusText = "";
            LogException("mods.log", ex);
        }
    }

    // ---- Mod detail overlay -----------------------------------------------

    [ObservableProperty] private ModDetailViewModel? _modDetail;

    public bool IsModDetailOpen => ModDetail is not null;

    partial void OnModDetailChanged(ModDetailViewModel? value) =>
        OnPropertyChanged(nameof(IsModDetailOpen));

    /// <summary>
    /// Opens the detail overlay for a search hit. Creates a fresh VM each
    /// time so the user always sees the latest version list and install state.
    /// </summary>
    void IModCommands.ShowModDetail(ModrinthSearchHit hit)
    {
        if (SelectedModInstance is null) return;
        var instance = SelectedModInstance.Model;
        var loader = instance.Modloader.ToString().ToLowerInvariant();
        var installed = _modInstaller.GetInstalledProjectIds(instance.Id);
        var detail = new ModDetailViewModel(
            hit, _modrinth, _http,
            instance.VersionId, loader,
            isInitiallyInstalled: installed.Contains(hit.ProjectId),
            commands: this);
        ModDetail = detail;
        _ = detail.LoadAsync();
    }

    void IModDetailCommands.CloseModDetail() => ModDetail = null;

    // Install one specific version from the detail view's picker. Reuses the
    // installer's manifest + dependency logic by going through InstallVersionAsync.
    async Task IModDetailCommands.InstallVersionAsync(string projectId, ModrinthVersion version)
    {
        if (SelectedModInstance is null) return;
        var instance = SelectedModInstance.Model;
        var loader = instance.Modloader.ToString().ToLowerInvariant();
        try
        {
            var progress = new Progress<ModInstallProgress>(p =>
                ModStatusText = p.Stage);
            await _modInstaller.InstallVersionAsync(
                projectId, version.Id, instance.Id, instance.VersionId, loader, progress, CancellationToken.None);
            RefreshInstalledMods();
            // After a successful install, mark the matching search-result card too
            // so closing the overlay doesn't leave a stale "설치" button visible.
            foreach (var card in ModSearchResults)
                if (string.Equals(card.Hit.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
                    card.IsInstalled = true;
            SetStatus($"'{ModDetail?.Title ?? projectId}' {version.VersionNumber} 설치됨", StatusKind.Success);
            ModStatusText = "";
        }
        catch (Exception ex)
        {
            SetStatus($"설치 실패: {ex.Message}", StatusKind.Error);
            ModStatusText = "";
            LogException("mods.log", ex);
        }
    }

    // ---- Update state ------------------------------------------------------

    [ObservableProperty] private UpdateInfo? _pendingUpdate;
    [ObservableProperty] private bool _isInstallingUpdate;
    [ObservableProperty] private double _updateDownloadProgress;

    public bool HasPendingUpdate => PendingUpdate is not null && !IsInstallingUpdate;

    partial void OnPendingUpdateChanged(UpdateInfo? value)
    {
        OnPropertyChanged(nameof(HasPendingUpdate));
        OnPropertyChanged(nameof(CanInstallUpdate));
        InstallUpdateCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsInstallingUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(HasPendingUpdate));
        OnPropertyChanged(nameof(CanInstallUpdate));
        InstallUpdateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Pretty version label shown in Settings → About and → 업데이트.</summary>
    public string AppBuildLabel => BuildInfo.IsDev
        ? "dev (로컬 빌드)"
        : $"{BuildInfo.ShortSha} · {SafeBuildDate()}";

    private static string SafeBuildDate()
    {
        var bt = BuildInfo.BuildTime;
        if (DateTimeOffset.TryParse(bt, out var d)) return d.ToLocalTime().ToString("yyyy-MM-dd");
        return bt.Length >= 10 ? bt[..10] : bt;
    }

    // Inline "new instance" form state for Settings > Instances.
    [ObservableProperty] private bool _isCreatingInstance;
    [ObservableProperty] private string _newInstanceName = "";
    [ObservableProperty] private string? _newInstanceVersion;
    [ObservableProperty] private string _newInstanceModloaderName = "Vanilla";

    public IReadOnlyList<string> ModloaderOptions { get; } = new[] { "Vanilla", "Fabric", "NeoForge" };

    /// <summary>One in-flight launch (prep + JVM). Multiple may be active at
    /// once now that PLAY no longer self-disables.</summary>
    public sealed record RunningLaunch(string RunId, string DisplayName);

    public ObservableCollection<RunningLaunch> RunningLaunches { get; } = [];

    /// <summary>True when at least one launch is mid-prep or mid-JVM. Used by
    /// the update-toast suppressor + the inline status ProgressBar so they
    /// keep working after we removed the single-LaunchPhase flag.</summary>
    public bool HasActiveLaunch => RunningLaunches.Count > 0;

    /// <summary>True when a launch with this RunId is already in flight.
    /// Used by the PLAY handler to put up a confirm-overlay before kicking off
    /// a duplicate.</summary>
    public bool IsLaunchActive(string runId) =>
        RunningLaunches.Any(r => string.Equals(r.RunId, runId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stable id for "is this combo already running?" lookups. Named
    /// instances key by instance id; quick-launch combos key by mc+loader so
    /// hitting PLAY twice in a row in quick mode still triggers the warning.</summary>
    private static string MakeRunId(string? instanceName, string versionId, Modloader modloader) =>
        instanceName ?? $"quick:{versionId}:{modloader}";

    // Always "PLAY" — the running-count was noisy. The status toast + the
    // duplicate-launch overlay already convey concurrent-launch state.
    public string LaunchButtonText => "PLAY";

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    public IBrush StatusToastBrush => StatusKind switch
    {
        StatusKind.Success => new SolidColorBrush(Color.Parse("#5BC54E")),
        StatusKind.Error   => new SolidColorBrush(Color.Parse("#EF5350")),
        StatusKind.Info    => new SolidColorBrush(Color.Parse("#FFB74D")),
        _                  => new SolidColorBrush(Color.Parse("#888888")),
    };

    public bool IsSignedIn => SignedInAccount is not null;

    public string SignedInUsername => SignedInAccount?.Username ?? "";

    // "Offline mode forced" flag — set when the user clicks 오프라인으로 플레이
    // even if they're already signed into Microsoft. Cleared when they
    // explicitly re-enter the MS account flow.
    [ObservableProperty] private bool _isOfflineMode;

    /// <summary>True when the Play view should ask for a username (offline).</summary>
    public bool ShowUsernameInput => !IsSignedIn || IsOfflineMode;

    /// <summary>True when the Play view should display the MS account chip.</summary>
    public bool ShowAccountChip => IsSignedIn && !IsOfflineMode;

    partial void OnIsOfflineModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowUsernameInput));
        OnPropertyChanged(nameof(ShowAccountChip));
        OnPropertyChanged(nameof(CanLaunch));
        LaunchCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Label on the Microsoft button in AccountSelection — switches to a
    /// "continue as" hint when the user is already signed in (e.g. after they
    /// hit "back" from the Play view).
    /// </summary>
    public string MicrosoftButtonText => IsSignedIn
        ? $"{SignedInUsername} 님으로 계속"
        : "Microsoft로 로그인";

    public bool IsWelcomeView    => CurrentView == AppView.Welcome;
    public bool IsAccountView    => CurrentView == AppView.AccountSelection;
    public bool IsJavaPlayView   => CurrentView == AppView.JavaPlay;
    public bool IsBedrockPlayView => CurrentView == AppView.BedrockPlay;
    public bool ShowBackButton   => CurrentView != AppView.Welcome;

    partial void OnCurrentViewChanged(AppView value)
    {
        OnPropertyChanged(nameof(IsWelcomeView));
        OnPropertyChanged(nameof(IsAccountView));
        OnPropertyChanged(nameof(IsJavaPlayView));
        OnPropertyChanged(nameof(IsBedrockPlayView));
        OnPropertyChanged(nameof(ShowBackButton));
    }

    public bool IsBedrockInstalled => BedrockInfo is not null;

    public string BedrockButtonText => IsBedrockInstalling
        ? "설치 중…"
        : IsBedrockInstalled
            ? "PLAY"
            : "설치";

    public string BedrockVersionText => IsBedrockInstalled
        ? $"최신 릴리즈 (v{BedrockInfo!.Version})"
        : "미설치";

    public string BedrockSubtitle => IsBedrockInstalling
        ? ""
        : IsBedrockInstalled
            ? ""
            : "Microsoft Store 없이 직접 설치";

    public bool CanBedrockAction => !IsBedrockInstalling;

    partial void OnBedrockInfoChanged(BedrockInstallInfo? value)
    {
        OnPropertyChanged(nameof(IsBedrockInstalled));
        OnPropertyChanged(nameof(BedrockButtonText));
        OnPropertyChanged(nameof(BedrockVersionText));
        OnPropertyChanged(nameof(BedrockSubtitle));
        BedrockActionCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBedrockInstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(BedrockButtonText));
        OnPropertyChanged(nameof(BedrockSubtitle));
        OnPropertyChanged(nameof(CanBedrockAction));
        BedrockActionCommand.NotifyCanExecuteChanged();
    }

    // Minecraft username rules: 3-16 chars, A-Z a-z 0-9 _
    public static bool IsValidUsernameChar(char c) =>
        (c is >= 'A' and <= 'Z')
        || (c is >= 'a' and <= 'z')
        || (c is >= '0' and <= '9')
        || c == '_';

    public bool IsUsernameValid =>
        Username.Length is >= 3 and <= 16
        && Username.All(IsValidUsernameChar);

    public string UsernameHint => Username.Length switch
    {
        0   => "3–16자 · A–Z 0–9 _",
        < 3 => "최소 3자 이상",
        _   => Username.All(IsValidUsernameChar) ? "" : "허용된 문자만 입력 가능",
    };

    public bool IsUsernameHintVisible => !string.IsNullOrEmpty(UsernameHint);

    public bool HasUsernameWarning => Username.Length > 0 && !IsUsernameValid;

    public string MojangStatusText => MojangStatus switch
    {
        MojangStatus.Operational => "로그인 서버 정상",
        MojangStatus.Degraded    => "로그인 서버 오류",
        MojangStatus.Down        => "로그인 서버 오류",
        _                        => "로그인 서버 확인 중…",
    };

    public IBrush MojangStatusBrush => MojangStatus switch
    {
        MojangStatus.Operational => new SolidColorBrush(Color.Parse("#4CAF50")),
        MojangStatus.Degraded    => new SolidColorBrush(Color.Parse("#FFA726")),
        MojangStatus.Down        => new SolidColorBrush(Color.Parse("#EF5350")),
        _                        => new SolidColorBrush(Color.Parse("#888888")),
    };

    public bool CanLaunch =>
        // IsIdle intentionally NOT checked — concurrent launches are allowed.
        // A duplicate of an already-running combo gets caught at click time by
        // the confirm-overlay (LaunchAsync), not by disabling the button.
        ((IsSignedIn && !IsOfflineMode) || IsUsernameValid)
        && (IsInstanceModeEnabled
            ? SelectedPlayInstance is not null
            : !string.IsNullOrWhiteSpace(SelectedVersion));

    // Note: LaunchPhase is no longer driving any UI; it's kept as a backing
    // field only so existing helper code (FriendlyLaunchError path, future
    // status diagnostics) can still observe the most recent transition.
    partial void OnLaunchPhaseChanged(LaunchState value) { }

    partial void OnSelectedVersionChanged(string? value)
    {
        LaunchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanLaunch));
    }

    partial void OnSignedInAccountChanged(MicrosoftAccount? value)
    {
        LaunchCommand.NotifyCanExecuteChanged();
        LaunchInstanceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(SignedInUsername));
        OnPropertyChanged(nameof(MicrosoftButtonText));
        OnPropertyChanged(nameof(ShowUsernameInput));
        OnPropertyChanged(nameof(ShowAccountChip));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(CanLaunchInstance));
    }

    partial void OnUsernameChanged(string value)
    {
        var clean = new string(value.Where(IsValidUsernameChar).Take(16).ToArray());
        if (clean != value)
        {
            Username = clean;
            return;
        }

        LaunchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(IsUsernameValid));
        OnPropertyChanged(nameof(UsernameHint));
        OnPropertyChanged(nameof(IsUsernameHintVisible));
        OnPropertyChanged(nameof(HasUsernameWarning));
    }

    partial void OnMojangStatusChanged(MojangStatus value)
    {
        OnPropertyChanged(nameof(MojangStatusText));
        OnPropertyChanged(nameof(MojangStatusBrush));
    }

    partial void OnStatusMessageChanged(string value) =>
        OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnStatusKindChanged(StatusKind value) =>
        OnPropertyChanged(nameof(StatusToastBrush));

    private CancellationTokenSource? _autoDismissCts;

    private void SetStatus(string message, StatusKind kind, bool autoDismiss = true)
    {
        _autoDismissCts?.Cancel();
        StatusMessage = message;
        StatusKind = kind;
        if (autoDismiss && !string.IsNullOrEmpty(message))
            ScheduleAutoDismiss(message);
    }

    private void ClearStatus()
    {
        _autoDismissCts?.Cancel();
        StatusMessage = "";
        StatusKind = StatusKind.None;
    }

    private void ScheduleAutoDismiss(string forMessage)
    {
        var ms = StatusKind == StatusKind.Error ? 8000 : 4000;
        var cts = new CancellationTokenSource();
        _autoDismissCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ms, cts.Token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!cts.IsCancellationRequested && StatusMessage == forMessage)
                    {
                        StatusMessage = "";
                        StatusKind = StatusKind.None;
                    }
                });
            }
            catch (OperationCanceledException) { /* superseded by newer status */ }
        });
    }

    public async Task InitializeAsync()
    {
        LoadInstances();
        await TryAutoSignInAsync();
        await LoadVersionsAsync();
        _ = Task.Run(MonitorStatusLoopAsync);
        _ = Task.Run(DetectBedrockAsync);

        // Single startup update check 30s after launch. No background polling —
        // user can re-trigger from Settings → 업데이트 → 지금 확인.
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30)); }
            catch (OperationCanceledException) { return; }
            try { await _updateService.CheckOnceAsync(CancellationToken.None); }
            catch { /* silent on failure — manual check still available */ }
        });
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        SetStatus("업데이트 확인 중…", StatusKind.Info);
        try
        {
            await _updateService.CheckOnceAsync(CancellationToken.None);
            if (PendingUpdate is null)
                SetStatus(BuildInfo.IsDev
                    ? "로컬 dev 빌드는 자동 업데이트 대상이 아닙니다."
                    : "최신 빌드를 사용 중입니다.", StatusKind.Success);
            // If PendingUpdate is set, the toast will appear via the existing event handler.
        }
        catch (Exception ex)
        {
            SetStatus($"확인 실패: {ex.Message}", StatusKind.Error);
        }
    }

    public bool CanInstallUpdate => PendingUpdate is not null && !IsInstallingUpdate;

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdate()
    {
        if (PendingUpdate is null) return;
        var info = PendingUpdate;
        IsInstallingUpdate = true;
        UpdateDownloadProgress = 0;
        try
        {
            var progress = new Progress<double>(p => UpdateDownloadProgress = p);
            await _updateService.DownloadAndInstallAsync(info, progress, CancellationToken.None);
            // DownloadAndInstallAsync calls Environment.Exit — nothing here runs.
        }
        catch (Exception ex)
        {
            IsInstallingUpdate = false;
            SetStatus($"업데이트 실패: {ex.Message}", StatusKind.Error);
        }
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        if (PendingUpdate is null) return;
        _updateService.Dismiss(PendingUpdate.ShortSha);
        PendingUpdate = null;
    }

    // ---- Instance lifecycle ---------------------------------------------------

    private void LoadInstances()
    {
        var loaded = _instanceStorage.Load();
        var scanned = _instanceStorage.ScanFilesystem(loaded.Select(i => i.Id));
        var all = loaded.Concat(scanned).OrderBy(i => i.CreatedAt).ToList();

        Instances.Clear();
        foreach (var model in all)
            Instances.Add(new InstanceItemViewModel(model, this));

        if (scanned.Count > 0)
            PersistInstances();
    }

    private void PersistInstances() =>
        _instanceStorage.SaveAll(Instances.Select(vm => vm.Model));

    public bool HasNoInstances => Instances.Count == 0 && !IsCreatingInstance;

    public bool CanCreateInstance =>
        !string.IsNullOrWhiteSpace(NewInstanceName)
        && !string.IsNullOrWhiteSpace(NewInstanceVersion);

    partial void OnNewInstanceNameChanged(string value) =>
        CreateInstanceCommand.NotifyCanExecuteChanged();

    partial void OnNewInstanceVersionChanged(string? value) =>
        CreateInstanceCommand.NotifyCanExecuteChanged();

    partial void OnIsCreatingInstanceChanged(bool value) =>
        OnPropertyChanged(nameof(HasNoInstances));

    [RelayCommand]
    private void BeginCreateInstance()
    {
        NewInstanceName = "";
        NewInstanceVersion = AvailableVersions.FirstOrDefault();
        NewInstanceModloaderName = "Vanilla";
        IsCreatingInstance = true;
    }

    [RelayCommand]
    private void CancelCreateInstance() => IsCreatingInstance = false;

    /// <summary>
    /// Pick a .mrpack file, extract the manifest, download every listed mod
    /// (parallel + multi-mirror fallback), copy overrides/ into the instance
    /// game dir, and register a fresh <see cref="Instance"/> when done. We
    /// generate the instance id from the filename so users can have multiple
    /// "Fabulously Optimized" instances coexisting.
    /// </summary>
    [RelayCommand]
    private async Task ImportMrpackAsync()
    {
        string? path;
        try { path = await _pickMrpackAsync(); }
        catch (Exception ex)
        {
            SetStatus($"파일 탐색기 열기 실패: {ex.Message}", StatusKind.Error);
            return;
        }
        if (path is null) return;

        SetStatus("모드팩 가져오기 시작…", StatusKind.Info, autoDismiss: false);

        try
        {
            // Derive a folder-safe instance id from the filename. GenerateId
            // handles non-ASCII and collisions automatically.
            var stem = Path.GetFileNameWithoutExtension(path);
            var existingIds = Instances.Select(vm => vm.Model.Id).ToList();
            var instanceId = InstanceStorage.GenerateId(stem, existingIds);

            var installer = new MrpackInstaller(_http);
            var progress = new Progress<MrpackProgress>(p =>
                SetStatus($"{p.Stage}", StatusKind.Info, autoDismiss: false));

            var result = await installer.ImportAsync(path, instanceId, progress, CancellationToken.None);

            // Register the new instance with the modloader the manifest declared.
            var model = new Instance
            {
                Id = instanceId,
                Name = result.ModpackName,
                VersionId = result.McVersion,
                Modloader = result.Modloader,
                CreatedAt = DateTimeOffset.Now,
            };
            Instances.Add(new InstanceItemViewModel(model, this));
            PersistInstances();

            var failedSuffix = result.FailedDownloads > 0
                ? $" · {result.FailedDownloads}개 다운로드 실패"
                : "";
            SetStatus(
                $"'{result.ModpackName}' 모드팩 가져옴 (모드 {result.ModFileCount}개, 설정 {result.OverrideFileCount}개){failedSuffix}",
                result.FailedDownloads > 0 ? StatusKind.Info : StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"모드팩 가져오기 실패: {ex.Message}", StatusKind.Error);
            LogException("mods.log", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateInstance))]
    private void CreateInstance()
    {
        var existingIds = Instances.Select(vm => vm.Model.Id);
        var id = InstanceStorage.GenerateId(NewInstanceName, existingIds);
        var model = new Instance
        {
            Id = id,
            Name = NewInstanceName.Trim(),
            VersionId = NewInstanceVersion!,
            Modloader = NewInstanceModloaderName switch
            {
                "Fabric"   => Modloader.Fabric,
                "NeoForge" => Modloader.NeoForge,
                _          => Modloader.Vanilla,
            },
            CreatedAt = DateTimeOffset.Now,
        };
        var card = new InstanceItemViewModel(model, this);
        Instances.Add(card);
        PersistInstances();

        // Seed the new instance's game dir with the user's vanilla settings
        // (options.txt + servers.dat) if a .minecraft install is present.
        // Non-overwriting — so re-creating an instance with an existing folder
        // doesn't clobber prior tweaks.
        try
        {
            var copied = VanillaImporter.SeedInstance(Paths.InstanceGameDir(model.Id));
            if (copied.Count > 0)
                SetStatus($"'{model.Name}' 생성됨 · vanilla 설정 가져옴 ({string.Join(", ", copied)})", StatusKind.Success);
            else
                SetStatus($"'{model.Name}' 생성됨", StatusKind.Success);
        }
        catch
        {
            SetStatus($"'{model.Name}' 생성됨", StatusKind.Success);
        }

        IsCreatingInstance = false;
    }

    // ---- IInstanceCommands ----------------------------------------------------

    void IInstanceCommands.OpenFolder(Instance instance)
    {
        try
        {
            var path = Paths.InstanceGameDir(instance.Id);
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus($"폴더 열기 실패: {ex.Message}", StatusKind.Error);
        }
    }

    async Task IInstanceCommands.DuplicateAsync(Instance source)
    {
        var newName = $"{source.Name} 사본";
        var existingIds = Instances.Select(vm => vm.Model.Id).ToList();
        var newId = InstanceStorage.GenerateId(newName, existingIds);
        var srcDir = Paths.InstanceDir(source.Id);
        var dstDir = Paths.InstanceDir(newId);

        SetStatus($"'{source.Name}' 복제 중…", StatusKind.Info, autoDismiss: false);
        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(srcDir))
                    CopyDirectory(srcDir, dstDir);
            });
            var dup = source with
            {
                Id = newId,
                Name = newName,
                CreatedAt = DateTimeOffset.Now,
                LastPlayedAt = null,
            };
            Instances.Add(new InstanceItemViewModel(dup, this));
            PersistInstances();
            SetStatus($"'{newName}' 생성됨", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"복제 실패: {ex.Message}", StatusKind.Error);
        }
    }

    void IInstanceCommands.Rename(Instance instance, string newName)
    {
        var card = Instances.FirstOrDefault(vm => vm.Model.Id == instance.Id);
        if (card is null) return;
        card.UpdateModel(card.Model with { Name = newName });
        PersistInstances();
    }

    Task IInstanceCommands.LaunchAsync(Instance instance)
    {
        // BeginLaunch fires-and-forgets internally and may throw up the
        // confirm overlay — there's nothing meaningful to await here.
        var card = Instances.FirstOrDefault(vm => vm.Model.Id == instance.Id);
        BeginLaunch(instance.VersionId, instance.Modloader, instance.Id, card);
        return Task.CompletedTask;
    }

    async Task IInstanceCommands.ExportAsync(Instance instance)
    {
        // Suggest a sensible default filename (instance id + iso date).
        var suggested = $"{instance.Id}-{DateTime.Now:yyyyMMdd}.zip";
        string? destPath;
        try { destPath = await _pickInstanceZipSaveAsync(suggested); }
        catch (Exception ex)
        {
            SetStatus($"파일 탐색기 열기 실패: {ex.Message}", StatusKind.Error);
            return;
        }
        if (destPath is null) return;

        SetStatus($"'{instance.Name}' 백업 중…", StatusKind.Info, autoDismiss: false);
        try
        {
            var backup = new InstanceBackup();
            var progress = new Progress<string>(msg => SetStatus(msg, StatusKind.Info, autoDismiss: false));
            await backup.ExportAsync(instance, destPath, progress, CancellationToken.None);

            var size = new FileInfo(destPath).Length;
            SetStatus(
                $"'{instance.Name}' 백업 완료 ({FormatBytes(size)})",
                StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"백업 실패: {ex.Message}", StatusKind.Error);
            LogException("instances.log", ex);
        }
    }

    /// <summary>Inverse of ExportAsync: pick a Mashiro instance zip and register
    /// it as a fresh instance. The new instance gets a unique id even if the
    /// archive came from the same machine.</summary>
    [RelayCommand]
    private async Task ImportInstanceAsync()
    {
        string? srcPath;
        try { srcPath = await _pickInstanceZipOpenAsync(); }
        catch (Exception ex)
        {
            SetStatus($"파일 탐색기 열기 실패: {ex.Message}", StatusKind.Error);
            return;
        }
        if (srcPath is null) return;

        SetStatus("인스턴스 백업 분석 중…", StatusKind.Info, autoDismiss: false);
        try
        {
            // We need a name hint to generate the new id. Use the zip filename
            // as a fallback — the actual display name comes from the manifest
            // once import finishes.
            var stem = Path.GetFileNameWithoutExtension(srcPath);
            var existingIds = Instances.Select(vm => vm.Model.Id).ToList();
            var newId = InstanceStorage.GenerateId(stem, existingIds);

            var backup = new InstanceBackup();
            var progress = new Progress<string>(msg => SetStatus(msg, StatusKind.Info, autoDismiss: false));
            var imported = await backup.ImportAsync(srcPath, newId, progress, CancellationToken.None);

            Instances.Add(new InstanceItemViewModel(imported, this));
            PersistInstances();
            SetStatus($"'{imported.Name}' 인스턴스 복원됨", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"가져오기 실패: {ex.Message}", StatusKind.Error);
            LogException("instances.log", ex);
        }
    }

    private static string FormatBytes(long bytes)
    {
        const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:0.##} GB",
            >= MB => $"{bytes / (double)MB:0.#} MB",
            >= KB => $"{bytes / (double)KB:0.#} KB",
            _     => $"{bytes} B",
        };
    }

    void IInstanceCommands.UpdateJvmOverrides(Instance instance, int? minMb, int? maxMb, string? customArgs)
    {
        var card = Instances.FirstOrDefault(vm => vm.Model.Id == instance.Id);
        if (card is null) return;
        card.UpdateModel(card.Model with
        {
            MinMemoryMb   = minMb,
            MaxMemoryMb   = maxMb,
            CustomJvmArgs = customArgs,
        });
        PersistInstances();
        SetStatus(
            (minMb is null && maxMb is null && string.IsNullOrEmpty(customArgs))
                ? $"'{card.Name}' 인스턴스 JVM 설정을 전역값으로 초기화했습니다"
                : $"'{card.Name}' 인스턴스 JVM 설정 저장됨",
            StatusKind.Info);
    }

    void IInstanceCommands.Delete(Instance instance)
    {
        var card = Instances.FirstOrDefault(vm => vm.Model.Id == instance.Id);
        if (card is null) return;
        try
        {
            var dir = Paths.InstanceDir(instance.Id);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            SetStatus($"폴더 삭제 실패: {ex.Message}", StatusKind.Error);
            return;
        }
        Instances.Remove(card);
        PersistInstances();
        SetStatus($"'{instance.Name}' 삭제됨", StatusKind.Info);
    }

    private static void CopyDirectory(string src, string dst)
    {
        var srcInfo = new DirectoryInfo(src);
        if (!srcInfo.Exists) return;
        Directory.CreateDirectory(dst);
        foreach (var file in srcInfo.GetFiles())
            file.CopyTo(Path.Combine(dst, file.Name), overwrite: false);
        foreach (var subDir in srcInfo.GetDirectories())
            CopyDirectory(subDir.FullName, Path.Combine(dst, subDir.Name));
    }

    // ---- Initialization -------------------------------------------------------

    private async Task DetectBedrockAsync()
    {
        var client = new BedrockClient();
        var info = await client.DetectAsync();
        await Dispatcher.UIThread.InvokeAsync(() => BedrockInfo = info);
    }

    private async Task TryAutoSignInAsync()
    {
        try
        {
            var account = await _msAuth.TryLoadAsync();
            if (account is null) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Remember the account but leave the user on the Welcome page.
                // When they pick "Java Edition", SelectJavaEdition will short-circuit
                // straight to JavaPlay because IsSignedIn is already true.
                SignedInAccount = account;
            });
        }
        catch
        {
            // ignore — user will see Welcome → AccountSelection path
        }
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            var svc = new VersionManifestService(_downloader);
            var manifest = await svc.GetAsync();
            var releases = manifest.Versions
                .Where(v => v.Type == "release")
                .Take(20)
                .Select(v => v.Id)
                .ToList();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AvailableVersions.Clear();
                foreach (var id in releases) AvailableVersions.Add(id);
                // Default both the Play view's version and the "new instance" form.
                SelectedVersion ??= manifest.Latest.Release;
                NewInstanceVersion ??= manifest.Latest.Release;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SetStatus($"버전 목록을 불러오지 못했습니다: {ex.Message}", StatusKind.Error);
            });
        }
    }

    private async Task MonitorStatusLoopAsync()
    {
        while (true)
        {
            MojangStatus status;
            try { status = await _statusService.CheckAsync(); }
            catch { status = MojangStatus.Unknown; }
            await Dispatcher.UIThread.InvokeAsync(() => MojangStatus = status);
            await Task.Delay(TimeSpan.FromSeconds(60));
        }
    }

    // ---- Launch flow (different instances concurrent, same instance blocked) ----
    //
    // The runId discrimination is per-instance (or per quick-launch combo).
    // Two different instances can launch in parallel without any prompt;
    // trying to launch one that's already in flight pops a "이미 실행 중"
    // info overlay and does nothing else — Minecraft's own session.lock would
    // refuse a second copy of the same game dir anyway, so blocking at the
    // launcher level is just clearer feedback.

    [ObservableProperty] private bool _isLaunchConfirmOpen;
    [ObservableProperty] private string _launchConfirmMessage = "";

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private void Launch()
    {
        string versionId;
        Modloader modloader;
        string? instanceName;
        InstanceItemViewModel? recordCard;

        if (IsInstanceModeEnabled)
        {
            if (SelectedPlayInstance is null) return;
            var inst = SelectedPlayInstance.Model;
            versionId    = inst.VersionId;
            modloader    = inst.Modloader;
            instanceName = inst.Id;
            recordCard   = SelectedPlayInstance;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(SelectedVersion)) return;
            versionId    = SelectedVersion!;
            modloader    = UseFabric ? Modloader.Fabric : Modloader.Vanilla;
            instanceName = null;
            recordCard   = null;
        }

        BeginLaunch(versionId, modloader, instanceName, recordCard);
    }

    public bool CanLaunchInstance => IsSignedIn || IsUsernameValid;

    [RelayCommand(CanExecute = nameof(CanLaunchInstance))]
    private void LaunchInstance(Instance instance)
    {
        var card = Instances.FirstOrDefault(vm => vm.Model.Id == instance.Id);
        BeginLaunch(instance.VersionId, instance.Modloader, instance.Id, card);
    }

    /// <summary>
    /// Entry point shared by PLAY and per-card ▶ 플레이. Builds a stable
    /// RunId, blocks the launch (with an info popup) when one with the same id
    /// is already in flight, otherwise kicks off LaunchInternalAsync fire-and-forget.
    /// </summary>
    private void BeginLaunch(
        string versionId, Modloader modloader, string? instanceName,
        InstanceItemViewModel? recordCard)
    {
        var runId = MakeRunId(instanceName, versionId, modloader);
        var displayName = recordCard?.Name ?? $"{modloader} · {versionId}";

        if (IsLaunchActive(runId))
        {
            LaunchConfirmMessage =
                $"'{displayName}'이(가) 이미 실행 중입니다.\n같은 인스턴스는 한 번에 하나만 실행할 수 있어요.";
            IsLaunchConfirmOpen = true;
            return;
        }

        // Fire-and-forget: the async path manages its own RunningLaunches entry
        // so the PLAY button returns to a clickable state immediately.
        _ = LaunchInternalAsync(versionId, modloader, instanceName, recordCard, runId, displayName);
    }

    /// <summary>Dismiss the "이미 실행 중" info overlay.</summary>
    [RelayCommand]
    private void DismissLaunchWarning() => IsLaunchConfirmOpen = false;

    private async Task LaunchInternalAsync(
        string versionId, Modloader modloader, string? instanceName,
        InstanceItemViewModel? recordCard, string runId, string displayName)
    {
        var launch = new RunningLaunch(runId, displayName);
        await Dispatcher.UIThread.InvokeAsync(() => RunningLaunches.Add(launch));

        // Status reflects THIS launch; if another runs in parallel its own
        // toast will overwrite — that's fine, the inline progress bar in the
        // toast plus the PLAY-button "N개 실행 중" label keep total state visible.
        SetStatus($"'{displayName}' 준비 중…", StatusKind.Info, autoDismiss: false);

        try
        {
            IAccount account = (!IsOfflineMode && SignedInAccount is not null)
                ? SignedInAccount
                : new OfflineAccount(Username);

            JvmOverrides? jvmOverrides = null;
            if (recordCard?.Model is { } inst &&
                (inst.MinMemoryMb is not null
                 || inst.MaxMemoryMb is not null
                 || !string.IsNullOrWhiteSpace(inst.CustomJvmArgs)))
            {
                jvmOverrides = new JvmOverrides(inst.MinMemoryMb, inst.MaxMemoryMb, inst.CustomJvmArgs);
            }

            var plan = await _launchService.PrepareAsync(
                versionId, account, modloader, instanceName, jvmOverrides, CancellationToken.None);

            SetStatus($"'{displayName}' 실행 중", StatusKind.Success);
            var exit = await _launchService.StartAsync(plan, CancellationToken.None);

            // Only named instances track last-played; quick launches don't.
            if (recordCard is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    recordCard.UpdateModel(recordCard.Model with { LastPlayedAt = DateTimeOffset.Now });
                    PersistInstances();
                });
            }

            SetStatus(
                exit == 0
                    ? $"'{displayName}' 종료"
                    : $"'{displayName}' 종료 (코드 {exit})",
                exit == 0 ? StatusKind.Info : StatusKind.Error);
        }
        catch (Exception ex)
        {
            SetStatus($"'{displayName}' 실행 실패: {FriendlyLaunchError(ex)}", StatusKind.Error);
            LogException("launch.log", ex);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => RunningLaunches.Remove(launch));
        }
    }

    /// <summary>Jump from the Play view to Settings → Instances for power-user flows.</summary>
    [RelayCommand]
    private void OpenInstanceManager()
    {
        SettingsCategory = SettingsCategory.Instances;
        IsSettingsOpen = true;
    }

    private static string FriendlyLaunchError(Exception ex)
    {
        var msg = ex.Message ?? "";
        if (ex is TaskCanceledException or OperationCanceledException)
            return "실행이 취소되었습니다";
        if (ex is HttpRequestException)
            return "서버와 연결이 끊겼습니다";
        if (msg.Contains("Fabric") && msg.Contains("지원하지 않습니다"))
            return msg;  // FabricMetaException already friendly
        if (ex.GetType().Name.Contains("FabricMeta", StringComparison.OrdinalIgnoreCase))
            return "Fabric 메타 서버 오류";
        if (ex.GetType().Name.Contains("HashMismatch"))
            return "다운로드 파일이 손상되었습니다";
        if (msg.Contains("Could not locate Java"))
            return "Java 실행 파일을 찾을 수 없습니다";
        return ContainsHangul(msg) && msg.Length < 120 ? msg : "알 수 없는 오류가 발생했습니다";
    }

    [RelayCommand]
    private async Task LoginMicrosoftAsync()
    {
        // Already signed in (e.g. user hit "back" from Play view) — skip the
        // WebView modal entirely and just resume their session.
        if (IsSignedIn)
        {
            IsOfflineMode = false;  // user explicitly chose the MS account path
            CurrentView = AppView.JavaPlay;
            ClearStatus();
            return;
        }

        IsAuthenticating = true;
        SetStatus("Microsoft 로그인 창을 여는 중…", StatusKind.Info, autoDismiss: false);
        try
        {
            var (url, state) = _msAuth.BuildAuthorizationUrl();

            // WebView2 modal pops up, drives the user through MS sign-in + Xbox
            // consent, and returns the captured redirect URL the moment Microsoft
            // attempts to navigate to oauth20_desktop.srf?code=...
            var redirectUrl = await WebViewLoginHelper.ShowAsync(
                url, MashiroLauncher.Core.Auth.Microsoft.MicrosoftAuthConfig.RedirectUri);

            if (string.IsNullOrEmpty(redirectUrl))
            {
                SetStatus("로그인이 취소되었습니다.", StatusKind.Info);
                return;
            }

            SetStatus("인증 중…", StatusKind.Info, autoDismiss: false);
            var account = await _msAuth.CompleteSignInAsync(redirectUrl, state, CancellationToken.None);
            SignedInAccount = account;
            IsOfflineMode = false;  // fresh sign-in clears any prior offline override
            CurrentView = AppView.JavaPlay;
            SetStatus($"{account.Username} 님으로 로그인했습니다.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"로그인 실패: {FriendlyAuthError(ex)}", StatusKind.Error);
            // MS auth is part of the launch path, so failures go into launch.log
            // (we used to keep a separate auth.log but that produced a noisy
            // viewer entry that was almost never useful in practice).
            LogException("launch.log", ex);
        }
        finally
        {
            IsAuthenticating = false;
        }
    }

    private static string FriendlyAuthError(Exception ex)
    {
        var msg = ex.Message ?? "";

        if (ex is TaskCanceledException or OperationCanceledException)
            return "로그인이 취소되었습니다";
        if (ex is HttpRequestException)
            return "서버와 연결이 끊겼습니다";
        if (msg.Contains(" 500:") || msg.Contains(" 502:") || msg.Contains(" 503:") || msg.Contains(" 504:"))
            return "인증 서버가 오프라인입니다";
        if (ex is MinecraftAuthException)
        {
            if (msg.Contains("Invalid app registration"))
                return "런처가 Microsoft 승인 대기 중입니다";
            if (msg.Contains("Java Edition"))
                return "이 계정은 Minecraft Java Edition을 보유하고 있지 않습니다";
            return "Minecraft 인증 서버 오류";
        }
        if (ex is XboxAuthException)
        {
            // MapXstsError already returns Korean for known XSTS codes.
            return ContainsHangul(msg) ? msg : "Xbox Live 인증 오류";
        }
        if (ex is OAuth2AuthException)
            return "Microsoft 인증 오류";
        return "알 수 없는 오류가 발생했습니다";
    }

    private static bool ContainsHangul(string s)
    {
        foreach (var c in s)
            if (c >= 0xAC00 && c <= 0xD7A3) return true;
        return false;
    }

    private static void LogException(string file, Exception ex)
    {
        try
        {
            var dir = Path.Combine(Paths.Data, "logs");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTimeOffset.Now:O}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n---\n";
            File.AppendAllText(Path.Combine(dir, file), line);
        }
        catch { /* logging is best-effort */ }
    }

    [RelayCommand(CanExecute = nameof(CanBedrockAction))]
    private async Task BedrockActionAsync()
    {
        if (IsBedrockInstalled)
        {
            try
            {
                new BedrockClient().Launch();
                SetStatus($"Bedrock 실행 (v{BedrockInfo?.Version})", StatusKind.Success);
            }
            catch (Exception ex)
            {
                SetStatus($"실행 실패: {ex.Message}", StatusKind.Error);
            }
            return;
        }

        IsBedrockInstalling = true;
        SetStatus("Bedrock 설치 준비 중…", StatusKind.Info, autoDismiss: false);
        try
        {
            var installer = new BedrockInstaller(_downloader);
            var progress = new Progress<BedrockInstallProgress>(p =>
            {
                SetStatus($"{p.Stage} ({p.Step}/{p.TotalSteps})", StatusKind.Info, autoDismiss: false);
            });
            await installer.InstallLatestAsync(progress, CancellationToken.None);
            await DetectBedrockAsync();
            SetStatus(
                BedrockInfo is null
                    ? "Bedrock 설치 완료 (재시작 후 인식)"
                    : $"Bedrock 설치 완료 (v{BedrockInfo.Version})",
                StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"Bedrock 설치 실패: {ex.Message}", StatusKind.Error);
            LogException("bedrock.log", ex);
        }
        finally
        {
            IsBedrockInstalling = false;
        }
    }

    [RelayCommand]
    private void SignOut()
    {
        _msAuth.SignOut();
        SignedInAccount = null;
        CurrentView = AppView.AccountSelection;
        SetStatus("로그아웃됨", StatusKind.Info);
    }

    [RelayCommand] private void OpenSettings() => IsSettingsOpen = true;
    [RelayCommand] private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void SelectJavaEdition()
    {
        // If a MS account was auto-loaded, skip straight to play.
        CurrentView = IsSignedIn ? AppView.JavaPlay : AppView.AccountSelection;
        ClearStatus();
    }

    [RelayCommand]
    private void SelectBedrockEdition()
    {
        CurrentView = AppView.BedrockPlay;
        ClearStatus();
    }

    [RelayCommand]
    private void GoBack()
    {
        CurrentView = CurrentView switch
        {
            AppView.JavaPlay         => AppView.AccountSelection,
            AppView.AccountSelection => AppView.Welcome,
            AppView.BedrockPlay      => AppView.Welcome,
            _                        => AppView.Welcome,
        };
        ClearStatus();
    }

    [RelayCommand]
    private void ChooseOffline()
    {
        // Force offline path even if the user is signed into Microsoft —
        // the launch will use OfflineAccount with the typed username.
        IsOfflineMode = true;
        CurrentView = AppView.JavaPlay;
        ClearStatus();
    }

    [RelayCommand]
    private async Task ChangeBackgroundAsync()
    {
        var path = await _pickImageAsync();
        if (path is null) return;
        try
        {
            await using var fs = File.OpenRead(path);
            await _backgroundService.SaveAsync(fs);
            BackgroundImage = _backgroundService.LoadCurrent();
        }
        catch (Exception ex)
        {
            SetStatus($"배경 이미지를 설정하지 못했습니다: {ex.Message}", StatusKind.Error);
        }
    }
}
