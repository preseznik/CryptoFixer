using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using CryptoFixer.Core.Backup;
using CryptoFixer.Core.Presets;
using CryptoFixer.Core.Schannel;
using CryptoFixer.Core.Settings;
using CryptoFixer.Services;

namespace CryptoFixer.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class MainViewModel : ObservableObject, IUpdateHost
{
    private readonly SchannelProtocolStore _store;
    private readonly WindowsVersionInfo _os;
    private readonly IDialogService _dialogs;
    private readonly ISystemActions _system;
    private IReadOnlyList<ProtocolRoleState> _states = [];
    private string _statusText = string.Empty;
    private bool _restartRequired;
    private int _pendingCount;
    private bool _closingForUpdate;

    private ProtocolFilter _filter = new();
    private string _searchText = string.Empty;
    private ProtocolStateFilter _stateFilter = ProtocolStateFilter.All;
    private RoleScope _roleScope = RoleScope.Both;
    private ProtocolSortKey _sortKey = ProtocolSortKey.Version;
    private bool _sortDescending;
    private int _visibleCount;

    private IReadOnlyList<ProtocolPreset> _userPresets = [];
    private string? _presetsError;

    private AppSettings _settings = AppSettings.Default;

    public MainViewModel(
        SchannelProtocolStore store,
        WindowsVersionInfo os,
        bool canWrite,
        string readOnlyReason,
        string backupFolder,
        string presetsPath,
        string settingsPath,
        IDialogService dialogs,
        ISystemActions system,
        UpdateSetup updates)
    {
        _store = store;
        _os = os;
        CanWrite = canWrite;
        ReadOnlyReason = readOnlyReason;
        BackupFolder = backupFolder;
        PresetsPath = presetsPath;
        SettingsPath = settingsPath;
        _dialogs = dialogs;
        _system = system;
        Updates = new UpdatesViewModel(updates, dialogs, system, this);

        var settings = AppSettingsFile.Load(SettingsPath);
        _settings = settings.Settings;

        RowsView = new ListCollectionView(Rows)
        {
            Filter = item => item is ProtocolRowViewModel row && _filter.Matches(row.ToView()),
            CustomSort = new RowComparer(new ProtocolRowComparer(_sortKey, _sortDescending)),
            IsLiveFiltering = true,
        };
        RowsView.LiveFilteringProperties.Add(nameof(ProtocolRowViewModel.HasPendingChanges));
        ((INotifyCollectionChanged)RowsView).CollectionChanged += (_, _) => UpdateVisibleCount();

        RefreshCommand = new RelayCommand(Refresh);
        ApplyCommand = new RelayCommand(Apply, () => CanWrite && HasPendingChanges);
        DiscardCommand = new RelayCommand(Discard, () => HasPendingChanges);
        ExportBackupCommand = new RelayCommand(ExportBackup);
        RestartCommand = new RelayCommand(Restart, () => CanWrite && RestartRequired);
        OpenBackupFolderCommand = new RelayCommand(() => _system.OpenFolder(BackupFolder));
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => IsFilterActive);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        ToggleSortDirectionCommand = new RelayCommand(() => SortDescending = !SortDescending);
        StagePresetCommand = new RelayCommand<ProtocolPreset>(StagePreset);
        DeletePresetCommand = new RelayCommand<ProtocolPreset>(DeletePreset, p => !p.IsBuiltIn);
        SavePresetCommand = new RelayCommand(SavePreset);
        ReloadPresetsCommand = new RelayCommand(() => StatusText = DescribeLoad(LoadPresets()));
        EditPresetsFileCommand = new RelayCommand(EditPresetsFile);

        Reload();
        BuiltInPresetList = BuiltInPresets.For(_states);

        StatusText = CanWrite
            ? "Choose a setting per protocol and role, or load a preset, then click Apply changes. Nothing is written until you apply."
            : $"{ReadOnlyReason} Preview mode: you can stage settings and presets to see their effect, but nothing can be applied.";

        if (updates.UpdatedFromMessage is not null)
        {
            StatusText = updates.UpdatedFromMessage;
        }

        var presets = LoadPresets();
        if (presets.Error is not null || presets.Warnings.Count > 0)
        {
            StatusText = DescribeLoad(presets);
        }

        if (settings.Error is not null || settings.Warnings.Count > 0)
        {
            StatusText = $"Settings file problem: {settings.Error ?? string.Join(" ", settings.Warnings)} Using defaults where needed. ({SettingsPath})";
        }
    }

    public ObservableCollection<ProtocolRowViewModel> Rows { get; } = [];

    /// <summary>Filtered and sorted view of <see cref="Rows"/> that the grid binds to.</summary>
    public ListCollectionView RowsView { get; }

    public bool CanWrite { get; }

    public bool IsReadOnly => !CanWrite;

    public string ReadOnlyReason { get; }

    public string ModeText => CanWrite ? "Administrator · changes enabled" : "Read-only · preview only";

    public string ApplyToolTip => CanWrite
        ? "Write the pending changes to the registry. A .reg backup of the current settings is saved first."
        : $"{ReadOnlyReason} Run CryptoFixer as Administrator to apply changes.";

    public string BackupFolder { get; }

    public string OsSummary => _os.Summary;

    public string DefaultsSummary => $"OS defaults shown for {OsDefaults.DescribeDefaultsFamily(_os.Build)}";

    public string RegistryPath => $@"HKEY_LOCAL_MACHINE\{SchannelPaths.Protocols}";

    public int PendingCount
    {
        get => _pendingCount;
        private set
        {
            if (SetProperty(ref _pendingCount, value))
            {
                OnPropertyChanged(nameof(HasPendingChanges));
                OnPropertyChanged(nameof(PendingSummary));
            }
        }
    }

    public bool HasPendingChanges => PendingCount > 0;

    public string PendingSummary => PendingCount switch
    {
        0 => "No pending changes",
        1 => "1 pending change",
        _ => $"{PendingCount} pending changes",
    };

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool RestartRequired
    {
        get => _restartRequired;
        private set
        {
            if (SetProperty(ref _restartRequired, value))
            {
                OnPropertyChanged(nameof(RestartNotice));
            }
        }
    }

    public string RestartNotice => RestartRequired
        ? "Changes were written to the registry. Restart Windows to activate them (restarting affected services alone is not sufficient for all applications)."
        : "Schannel reads these settings when Windows starts. Any change takes effect only after Windows is restarted.";

    // ---- Filtering and sorting ----

    public IReadOnlyList<Choice<ProtocolStateFilter>> StateFilterOptions { get; } =
    [
        new(ProtocolStateFilter.All, "All protocols"),
        new(ProtocolStateFilter.Enabled, "Enabled"),
        new(ProtocolStateFilter.Disabled, "Disabled"),
        new(ProtocolStateFilter.NotSupported, "Not supported"),
        new(ProtocolStateFilter.SetInRegistry, "Set in registry"),
        new(ProtocolStateFilter.OsDefault, "Using OS default"),
        new(ProtocolStateFilter.PendingChanges, "Pending changes"),
        new(ProtocolStateFilter.Deprecated, "Deprecated protocols"),
    ];

    public IReadOnlyList<Choice<RoleScope>> RoleScopeOptions { get; } =
    [
        new(RoleScope.Both, "Server and client"),
        new(RoleScope.Server, "Server only"),
        new(RoleScope.Client, "Client only"),
    ];

    public IReadOnlyList<Choice<ProtocolSortKey>> SortOptions { get; } =
    [
        new(ProtocolSortKey.Version, "Protocol version"),
        new(ProtocolSortKey.Name, "Name"),
        new(ProtocolSortKey.ServerState, "Server state"),
        new(ProtocolSortKey.ClientState, "Client state"),
        new(ProtocolSortKey.ServerSource, "Server: set in registry first"),
        new(ProtocolSortKey.ClientSource, "Client: set in registry first"),
    ];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyView();
            }
        }
    }

    public ProtocolStateFilter StateFilter
    {
        get => _stateFilter;
        set
        {
            if (SetProperty(ref _stateFilter, value))
            {
                ApplyView();
            }
        }
    }

    /// <summary>Which side the state filter applies to; the other side's column is hidden.</summary>
    public RoleScope RoleScope
    {
        get => _roleScope;
        set
        {
            if (SetProperty(ref _roleScope, value))
            {
                OnPropertyChanged(nameof(ShowServerColumn));
                OnPropertyChanged(nameof(ShowClientColumn));
                ApplyView();
            }
        }
    }

    public ProtocolSortKey SortKey
    {
        get => _sortKey;
        set => SetSort(value, SortDescending);
    }

    public bool SortDescending
    {
        get => _sortDescending;
        set => SetSort(SortKey, value);
    }

    public string SortDirectionGlyph => SortDescending ? "↓" : "↑";

    public string SortDirectionToolTip => SortDescending ? "Descending (click for ascending)" : "Ascending (click for descending)";

    public bool ShowServerColumn => RoleScope != RoleScope.Client;

    public bool ShowClientColumn => RoleScope != RoleScope.Server;

    public bool IsFilterActive => _filter.IsActive;

    public bool HasNoVisibleRows => Rows.Count > 0 && _visibleCount == 0;

    public string VisibleCountText => IsFilterActive
        ? $"Showing {_visibleCount} of {Rows.Count} protocols"
        : $"{Rows.Count} protocols";

    // ---- Display mode ----

    public IReadOnlyList<Choice<TableDisplayMode>> DisplayModeOptions { get; } =
    [
        new(TableDisplayMode.Standard, "Standard"),
        new(TableDisplayMode.Compact, "Compact"),
        new(TableDisplayMode.List, "List"),
    ];

    /// <summary>Table layout; remembered in the portable settings file next to the executable.</summary>
    public TableDisplayMode DisplayMode
    {
        get => _settings.DisplayMode;
        set
        {
            if (value == _settings.DisplayMode || !Enum.IsDefined(value))
            {
                return;
            }

            _settings = _settings with { DisplayMode = value };
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsListMode));
            OnPropertyChanged(nameof(IsTableMode));
            SaveSettings();
        }
    }

    public bool IsListMode => DisplayMode == TableDisplayMode.List;

    public bool IsTableMode => !IsListMode;

    /// <summary>Whether to look for a new release in the background at startup; remembered in the settings file.</summary>
    public bool CheckForUpdatesAtStartup
    {
        get => _settings.CheckForUpdates;
        set
        {
            if (value == _settings.CheckForUpdates)
            {
                return;
            }

            _settings = _settings with { CheckForUpdates = value };
            OnPropertyChanged();
            SaveSettings();
            StatusText = value
                ? "CryptoFixer will check for updates when it starts."
                : "Automatic update checks are off. Use Updates ▸ Check for updates now to check manually.";
        }
    }

    public UpdatesViewModel Updates { get; }

    string? IUpdateHost.PendingChangesWarning => HasPendingChanges
        ? $"You have {PendingSummary.ToLowerInvariant()} that weren't applied. Restarting CryptoFixer discards them."
        : null;

    void IUpdateHost.ReportStatus(string message) => StatusText = message;

    void IUpdateHost.PrepareForRestart() => _closingForUpdate = true;

    public string SettingsPath { get; }

    // ---- Presets ----

    public string PresetsPath { get; }

    public bool PresetsFileExists => File.Exists(PresetsPath);

    public IReadOnlyList<ProtocolPreset> BuiltInPresetList { get; }

    public IReadOnlyList<ProtocolPreset> UserPresets
    {
        get => _userPresets;
        private set => SetProperty(ref _userPresets, value);
    }

    /// <summary>Set when the presets file exists but couldn't be read.</summary>
    public string? PresetsError
    {
        get => _presetsError;
        private set => SetProperty(ref _presetsError, value);
    }

    public ICommand RefreshCommand { get; }

    public ICommand ApplyCommand { get; }

    public ICommand DiscardCommand { get; }

    public ICommand ExportBackupCommand { get; }

    public ICommand RestartCommand { get; }

    public ICommand OpenBackupFolderCommand { get; }

    public ICommand ClearFiltersCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand ToggleSortDirectionCommand { get; }

    public ICommand StagePresetCommand { get; }

    public ICommand DeletePresetCommand { get; }

    public ICommand SavePresetCommand { get; }

    public ICommand ReloadPresetsCommand { get; }

    public ICommand EditPresetsFileCommand { get; }

    public IReadOnlyList<PendingChange> GetPendingChanges() =>
        Rows.SelectMany(r => new[] { r.Server, r.Client })
            .Select(c => c.ToPendingChange())
            .OfType<PendingChange>()
            .ToList();

    /// <summary>Returns false if the user cancelled closing because of pending changes.</summary>
    public bool ConfirmClose() =>
        _closingForUpdate || !CanWrite || !HasPendingChanges ||
        _dialogs.Confirm("Discard pending changes?", $"You have {PendingSummary.ToLowerInvariant()} that were not applied. Exit anyway?");

    /// <summary>Column header click: ascending, then descending, then back to protocol version order.</summary>
    public void SortByColumn(ProtocolSortKey key)
    {
        if (SortKey != key)
        {
            SetSort(key, descending: false);
        }
        else if (!SortDescending)
        {
            SetSort(key, descending: true);
        }
        else
        {
            SetSort(ProtocolSortKey.Version, descending: false);
        }
    }

    private void SetSort(ProtocolSortKey key, bool descending)
    {
        if (key == _sortKey && descending == _sortDescending)
        {
            return;
        }

        _sortKey = key;
        _sortDescending = descending;
        ApplyView();
        OnPropertyChanged(nameof(SortKey));
        OnPropertyChanged(nameof(SortDescending));
        OnPropertyChanged(nameof(SortDirectionGlyph));
        OnPropertyChanged(nameof(SortDirectionToolTip));
    }

    private void ApplyView()
    {
        _filter = new ProtocolFilter(SearchText, StateFilter, RoleScope);
        using (RowsView.DeferRefresh())
        {
            RowsView.CustomSort = new RowComparer(new ProtocolRowComparer(SortKey, SortDescending));
        }

        OnPropertyChanged(nameof(IsFilterActive));
        UpdateVisibleCount();
    }

    private void UpdateVisibleCount()
    {
        _visibleCount = RowsView.Count;
        OnPropertyChanged(nameof(VisibleCountText));
        OnPropertyChanged(nameof(HasNoVisibleRows));
    }

    private void SaveSettings()
    {
        try
        {
            AppSettingsFile.Save(SettingsPath, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Settings can't be saved: writing {SettingsPath} failed ({ex.Message}).";
        }
    }

    private void ClearFilters()
    {
        _searchText = string.Empty;
        _stateFilter = ProtocolStateFilter.All;
        _roleScope = RoleScope.Both;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(StateFilter));
        OnPropertyChanged(nameof(RoleScope));
        OnPropertyChanged(nameof(ShowServerColumn));
        OnPropertyChanged(nameof(ShowClientColumn));
        ApplyView();
    }

    private void Reload()
    {
        _states = _store.ReadAll(_os.Build);
        Rows.Clear();
        foreach (var protocol in ProtocolCatalog.All)
        {
            var server = _states.Single(s => s.Protocol == protocol && s.Role == ProtocolRole.Server);
            var client = _states.Single(s => s.Protocol == protocol && s.Role == ProtocolRole.Client);
            Rows.Add(new ProtocolRowViewModel(
                protocol,
                new ProtocolCellViewModel(server, OnCellChanged),
                new ProtocolCellViewModel(client, OnCellChanged)));
        }

        OnCellChanged();
        UpdateVisibleCount();
    }

    private void OnCellChanged()
    {
        PendingCount = GetPendingChanges().Count;
        CommandManager.InvalidateRequerySuggested();
    }

    private void Refresh()
    {
        if (HasPendingChanges && !_dialogs.Confirm("Refresh", "Refreshing discards your pending changes. Continue?"))
        {
            return;
        }

        Reload();
        StatusText = $"Reloaded settings from the registry at {DateTime.Now:T}.";
    }

    private void Discard()
    {
        Reload();
        StatusText = "Pending changes discarded.";
    }

    // ---- Presets ----

    private void StagePreset(ProtocolPreset preset)
    {
        var plan = PresetPlanner.Plan(preset, _states);
        var changed = 0;
        foreach (var target in plan.Targets)
        {
            var cell = Rows.First(r => r.Protocol == target.Protocol).Cell(target.Role);
            if (cell.Selected != target.Target)
            {
                cell.Selected = target.Target;
                changed++;
            }
        }

        var message = new StringBuilder($"Preset \"{preset.Name}\": ");
        message.Append(changed == 0
            ? "the selected settings already match it."
            : $"staged {changed} setting(s); {PendingSummary.ToLowerInvariant()} in total.");

        if (plan.Skipped.Count > 0)
        {
            message.Append($" Skipped {string.Join("; ", plan.Skipped)}.");
        }

        if (IsFilterActive && Rows.Any(r => r.HasPendingChanges && !_filter.Matches(r.ToView())))
        {
            message.Append(" Some rows with pending changes are hidden by the current filter.");
        }

        if (HasPendingChanges)
        {
            message.Append(CanWrite ? " Review and click Apply changes." : " Preview only: run as Administrator to apply.");
        }

        StatusText = message.ToString();
    }

    private void SavePreset()
    {
        if (PresetsError is not null && File.Exists(PresetsPath))
        {
            if (!_dialogs.Confirm(
                    "Presets file can't be read",
                    $"{PresetsError}\n\n{PresetsPath}\n\nRename the unreadable file to \"{Path.GetFileName(PresetsPath)}.invalid-<date>\" and start a new presets file?",
                    isWarning: true))
            {
                return;
            }

            try
            {
                var moved = PresetFile.MoveAside(PresetsPath, DateTime.Now);
                LoadPresets();
                StatusText = $"Moved the unreadable presets file to {moved}.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dialogs.ShowError("Presets file", $"Couldn't rename {PresetsPath}: {ex.Message}");
                return;
            }
        }

        var details = _dialogs.AskPresetDetails($"{Environment.MachineName} {DateTime.Now:yyyy-MM-dd}", PresetsPath, PresetFile.ValidateName);
        if (details is null)
        {
            return;
        }

        var existing = UserPresets.FirstOrDefault(p => string.Equals(p.Name, details.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !_dialogs.Confirm("Replace preset", $"A preset named \"{existing.Name}\" already exists. Replace it?"))
        {
            return;
        }

        var settings = Rows
            .SelectMany(r => new[] { r.Server, r.Client })
            .Select(c => new PresetEntry(c.State.Protocol, c.State.Role, c.Selected));
        var preset = ProtocolPreset.Capture(details.Name, details.Description, settings, DateTimeOffset.Now, $"{Environment.MachineName}, {_os.Summary}", out var skipped);

        var presets = UserPresets.ToList();
        var index = existing is null ? -1 : presets.IndexOf(existing);
        if (index >= 0)
        {
            presets[index] = preset;
        }
        else
        {
            presets.Add(preset);
        }

        if (!TrySavePresets(presets))
        {
            return;
        }

        StatusText = $"Saved preset \"{preset.Name}\" ({preset.Entries.Count} settings) to {PresetsPath}." +
                     (skipped.Count > 0 ? $" {skipped.Count} custom value(s) can't be stored in a preset and were left out." : string.Empty);
    }

    private void DeletePreset(ProtocolPreset preset)
    {
        if (!_dialogs.Confirm("Delete preset", $"Delete the preset \"{preset.Name}\" from {Path.GetFileName(PresetsPath)}?"))
        {
            return;
        }

        var remaining = UserPresets.Where(p => !string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (TrySavePresets(remaining))
        {
            StatusText = $"Deleted preset \"{preset.Name}\".";
        }
    }

    private void EditPresetsFile()
    {
        if (!PresetsFileExists && !TrySavePresets([]))
        {
            return;
        }

        try
        {
            _system.OpenInTextEditor(PresetsPath);
            StatusText = "Opened the presets file in Notepad. After saving your edits, choose Presets ▸ Reload presets file.";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Open presets file", ex.Message);
        }
    }

    private bool TrySavePresets(IReadOnlyList<ProtocolPreset> presets)
    {
        try
        {
            PresetFile.Save(PresetsPath, presets);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Couldn't save presets",
                $"Writing {PresetsPath} failed: {ex.Message}\n\n" +
                "Presets are stored next to CryptoFixer.exe so they travel with it. If the app is in a protected folder such as " +
                "Program Files, run it as Administrator or copy it to a writable folder.");
            return false;
        }

        LoadPresets();
        return true;
    }

    private PresetFileLoadResult LoadPresets()
    {
        var result = PresetFile.Load(PresetsPath);
        UserPresets = result.Presets;
        PresetsError = result.Error;
        OnPropertyChanged(nameof(PresetsFileExists));
        return result;
    }

    private string DescribeLoad(PresetFileLoadResult result)
    {
        if (result.Error is not null)
        {
            return $"Presets file problem: {result.Error} Built-in presets are still available. ({PresetsPath})";
        }

        if (!result.FileExists)
        {
            return $"No presets file yet. Saved presets are stored in {PresetsPath}.";
        }

        var text = $"Loaded {result.Presets.Count} preset(s) from {Path.GetFileName(PresetsPath)}";
        return result.Warnings.Count == 0
            ? text + "."
            : $"{text} with {result.Warnings.Count} warning(s): {string.Join(" ", result.Warnings)}";
    }

    // ---- Apply / backup ----

    private void Apply()
    {
        var changes = GetPendingChanges();
        if (changes.Count == 0 || !CanWrite)
        {
            return;
        }

        var warnings = ChangeAdvisor.GetWarnings(_states, changes);
        var message = new StringBuilder();
        message.AppendLine($"Write {changes.Count} change(s) to {RegistryPath}?");
        message.AppendLine();
        foreach (var change in changes)
        {
            message.AppendLine($"  • {change}");
        }

        if (warnings.Count > 0)
        {
            message.AppendLine();
            message.AppendLine("Warnings:");
            foreach (var warning in warnings)
            {
                message.AppendLine($"  ⚠ {warning}");
            }
        }

        message.AppendLine();
        message.AppendLine($"A backup of the current settings will first be saved to:\n{BackupFolder}");
        message.AppendLine();
        message.Append("Windows must be restarted for the changes to take effect.");

        if (!_dialogs.Confirm("Apply changes", message.ToString(), isWarning: warnings.Count > 0))
        {
            return;
        }

        string backupPath;
        try
        {
            backupPath = WriteBackup(Path.Combine(BackupFolder, DefaultBackupFileName()));
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Backup failed", $"Could not save the registry backup, so nothing was changed.\n\n{ex.Message}");
            return;
        }

        try
        {
            _store.Apply(changes);
        }
        catch (Exception ex)
        {
            Reload();
            StatusText = $"Applying failed. Backup: {backupPath}";
            _dialogs.ShowError("Apply failed",
                $"Writing to the registry failed: {ex.Message}\n\nSome changes may already have been written. " +
                $"The previous settings can be restored from:\n{backupPath}");
            return;
        }

        Reload();
        RestartRequired = true;
        StatusText = $"Applied {changes.Count} change(s). Backup saved to {backupPath}. Restart Windows to activate the changes.";

        if (_dialogs.Confirm("Restart required",
                $"{changes.Count} change(s) were applied.\nBackup: {backupPath}\n\n" +
                "Schannel only picks up protocol changes after a restart. Restart Windows now?"))
        {
            _system.RestartComputer();
        }
    }

    private void ExportBackup()
    {
        var path = _dialogs.PickSaveFile(BackupFolder, DefaultBackupFileName());
        if (path is null)
        {
            return;
        }

        try
        {
            WriteBackup(path);
            StatusText = $"Backup exported to {path}.";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Export failed", ex.Message);
        }
    }

    private void Restart()
    {
        if (_dialogs.Confirm("Restart Windows", "Restart Windows now to activate the Schannel changes? Save your work in other applications first.", isWarning: true))
        {
            _system.RestartComputer();
        }
    }

    private string WriteBackup(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var content = RegFileExporter.ExportProtocols(_store.Registry, DateTimeOffset.Now, Environment.MachineName);
        RegFileExporter.WriteFile(path, content);
        return path;
    }

    private static string DefaultBackupFileName() =>
        $"Schannel-Protocols-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.reg";

    private sealed class RowComparer(ProtocolRowComparer inner) : IComparer
    {
        public int Compare(object? x, object? y) =>
            inner.Compare((x as ProtocolRowViewModel)?.ToView(), (y as ProtocolRowViewModel)?.ToView());
    }
}

/// <summary>Text shown for presets in menus and tooltips.</summary>
public static class PresetText
{
    public static string MenuLabel(ProtocolPreset preset) =>
        preset.IsBuiltIn ? $"{preset.Name}  (built-in)" : preset.Name;

    public static string Describe(ProtocolPreset preset)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(preset.Description))
        {
            text.AppendLine(preset.Description).AppendLine();
        }

        foreach (var group in preset.Entries.GroupBy(e => e.Protocol))
        {
            var server = group.FirstOrDefault(e => e.Role == ProtocolRole.Server);
            var client = group.FirstOrDefault(e => e.Role == ProtocolRole.Client);
            var value = server is not null && client is not null && server.Setting == client.Setting
                ? Label(server.Setting)
                : string.Join(", ", group.Select(e => $"{e.Role.ToString().ToLowerInvariant()} {Label(e.Setting)}"));
            text.AppendLine($"{group.Key.KeyName}: {value}");
        }

        if (preset.Entries.Count == 0)
        {
            text.AppendLine("(no protocol settings)");
        }

        if (preset.SavedAt is not null || preset.SavedFrom is not null)
        {
            text.AppendLine().Append("Saved");
            if (preset.SavedAt is { } savedAt)
            {
                text.Append($" {savedAt.LocalDateTime:g}");
            }

            if (preset.SavedFrom is { } from)
            {
                text.Append($" on {from}");
            }
        }

        text.AppendLine().AppendLine().Append("Loading only stages these settings; protocols not listed are left unchanged.");
        return text.ToString().Trim();
    }

    private static string Label(ConfiguredSetting setting) => setting switch
    {
        ConfiguredSetting.Enabled => "enabled",
        ConfiguredSetting.Disabled => "disabled",
        ConfiguredSetting.NotConfigured => "OS default",
        _ => "custom",
    };
}
