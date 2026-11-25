using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Security;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using CryptoFixer.Core.Backup;
using CryptoFixer.Core.Presets;
using CryptoFixer.Core.Remote;
using CryptoFixer.Core.Schannel;
using CryptoFixer.Core.Settings;
using CryptoFixer.Services;

namespace CryptoFixer.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public enum ConnectionState
{
    Ready,
    Connecting,
    Failed,
}

public sealed class MainViewModel : ObservableObject, IUpdateHost, IMachineHost
{
    private const string LocalKey = "\0local";
    private const string RemoteRestartComment = "CryptoFixer: restarting to apply Schannel changes.";

    private static readonly SchannelSnapshot EmptySnapshot = new([], [], ActiveCipherSuites.Unavailable);

    private readonly SchannelTarget _localTarget;
    private readonly RemoteSetup _remote;
    private readonly IDialogService _dialogs;
    private readonly ISystemActions _system;
    private readonly HashSet<string> _restartPending = new(StringComparer.OrdinalIgnoreCase);

    // The values read from the computer shown (last connect, refresh or apply).
    private SchannelSnapshot _snapshot = EmptySnapshot;
    private string _statusText = string.Empty;
    private int _pendingCount;
    private bool _closingForUpdate;

    // The computer shown: null while connecting to a remote machine or after a failed connection.
    private SchannelTarget? _target;
    private MachineItemViewModel _currentMachine;
    private ConnectionState _connectionState = ConnectionState.Ready;
    private string _connectionTitle = string.Empty;
    private string _connectionMessage = string.Empty;
    private string? _connectionDetail;
    private int _switchGeneration;
    private CancellationTokenSource? _connectCancellation;
    private bool _isBusy;
    private string _busyText = string.Empty;

    private ProtocolFilter _filter = new();
    private string _searchText = string.Empty;
    private ProtocolStateFilter _stateFilter = ProtocolStateFilter.All;
    private RoleScope _roleScope = RoleScope.Both;
    private ProtocolSortKey _sortKey = ProtocolSortKey.Version;
    private bool _sortDescending;
    private AlgorithmSortKey _algorithmSortKey = AlgorithmSortKey.Strength;
    private bool _algorithmSortDescending;
    private int _visibleCount;
    private int _algorithmVisibleCount;
    private SettingsSection _section = SettingsSection.Protocols;

    private IReadOnlyList<ProtocolPreset> _builtInPresets = [];
    private IReadOnlyList<ProtocolPreset> _userPresets = [];
    private string? _presetsError;

    private AppSettings _settings = AppSettings.Default;

    public MainViewModel(
        SchannelTarget localTarget,
        RemoteSetup remote,
        string backupFolder,
        string presetsPath,
        string settingsPath,
        IDialogService dialogs,
        ISystemActions system,
        UpdateSetup updates)
    {
        _localTarget = localTarget;
        _target = localTarget;
        _remote = remote;
        BackupFolder = backupFolder;
        PresetsPath = presetsPath;
        SettingsPath = settingsPath;
        _dialogs = dialogs;
        _system = system;
        Updates = new UpdatesViewModel(updates, dialogs, system, this);
        Machines = new MachinesViewModel(remote.MachinesPath, dialogs, remote.Protector, this, localTarget.HostName);
        _currentMachine = Machines.Local;

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

        AlgorithmRowsView = new ListCollectionView(AlgorithmRows)
        {
            Filter = item => item is AlgorithmRowViewModel row && row.Category == CurrentCategory && _filter.Matches(row.ToView()),
            CustomSort = new AlgorithmComparer(new AlgorithmRowComparer(_algorithmSortKey, _algorithmSortDescending)),
            IsLiveFiltering = true,
        };
        AlgorithmRowsView.LiveFilteringProperties.Add(nameof(AlgorithmRowViewModel.HasPendingChanges));
        ((INotifyCollectionChanged)AlgorithmRowsView).CollectionChanged += (_, _) => UpdateVisibleCount();

        Sections =
        [
            new(SettingsSection.Protocols, "Protocols", @"SCHANNEL\Protocols: SSL, TLS, DTLS and PCT versions, per server and client side"),
            new(SettingsSection.Ciphers, "Ciphers", @"SCHANNEL\Ciphers: bulk encryption (NULL, DES, RC2, RC4, Triple DES, AES)"),
            new(SettingsSection.Hashes, "Hashes", @"SCHANNEL\Hashes: MD5, SHA-1 and SHA-2 message authentication"),
            new(SettingsSection.KeyExchange, "Key exchange", @"SCHANNEL\KeyExchangeAlgorithms: PKCS (RSA), Diffie-Hellman and ECDH"),
        ];

        RefreshCommand = new RelayCommand(Refresh, () => !IsBusy && !IsConnecting);
        ApplyCommand = new RelayCommand(Apply, () => CanWrite && HasPendingChanges && !IsBusy);
        DiscardCommand = new RelayCommand(Discard, () => HasPendingChanges && !IsBusy);
        ExportBackupCommand = new RelayCommand(ExportBackup, () => HasTarget && !IsBusy);
        RestartCommand = new RelayCommand(Restart, () => CanWrite && RestartRequired && !IsBusy);
        OpenBackupFolderCommand = new RelayCommand(() => _system.OpenFolder(BackupFolder));
        ClearFiltersCommand = new RelayCommand(ClearFilters, () => IsFilterActive);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        ToggleSortDirectionCommand = new RelayCommand(() =>
        {
            if (IsProtocolSection)
            {
                SortDescending = !SortDescending;
            }
            else
            {
                AlgorithmSortDescending = !AlgorithmSortDescending;
            }
        });
        StagePresetCommand = new RelayCommand<ProtocolPreset>(StagePreset, _ => HasTarget && !IsBusy);
        DeletePresetCommand = new RelayCommand<ProtocolPreset>(DeletePreset, p => !p.IsBuiltIn);
        SavePresetCommand = new RelayCommand(SavePreset, () => HasTarget && !IsBusy);
        ReloadPresetsCommand = new RelayCommand(() => StatusText = DescribeLoad(LoadPresets()));
        EditPresetsFileCommand = new RelayCommand(EditPresetsFile);
        RetryConnectCommand = new RelayCommand(() => TrySwitch(_currentMachine, force: true), () => IsRemoteTarget && !IsBusy && !IsConnecting);
        EditCurrentMachineCommand = new RelayCommand(() => Machines.EditCommand.Execute(_currentMachine), () => IsRemoteTarget && !IsBusy);
        BackToLocalCommand = new RelayCommand(() => TrySwitch(Machines.Local), () => IsRemoteTarget && !IsBusy);

        PopulateRows(localTarget.Store.ReadSnapshot(localTarget.Os.Build));

        StatusText = CanWrite
            ? "Choose settings for protocols, ciphers, hashes and key exchange, or load a preset, then click Apply changes. Nothing is written until you apply."
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

        if (Machines.Load() is { } machinesProblem)
        {
            StatusText = machinesProblem;
        }

        if (settings.Error is not null || settings.Warnings.Count > 0)
        {
            StatusText = $"Settings file problem: {settings.Error ?? string.Join(" ", settings.Warnings)} Using defaults where needed. ({SettingsPath})";
        }
    }

    public ObservableCollection<ProtocolRowViewModel> Rows { get; } = [];

    /// <summary>Filtered and sorted view of <see cref="Rows"/> that the grid binds to.</summary>
    public ListCollectionView RowsView { get; }

    /// <summary>Ciphers, hashes and key exchange algorithms of the computer shown.</summary>
    public ObservableCollection<AlgorithmRowViewModel> AlgorithmRows { get; } = [];

    /// <summary>The rows of the selected algorithm section, filtered and sorted.</summary>
    public ListCollectionView AlgorithmRowsView { get; }

    public MachinesViewModel Machines { get; }

    /// <summary>Changes can be applied to the computer shown (elevated locally, not --read-only, and connected).</summary>
    public bool CanWrite => _target?.CanWrite ?? false;

    public bool IsReadOnly => !CanWrite;

    public string ReadOnlyReason => _target?.ReadOnlyReason ?? string.Empty;

    public string ModeText => IsRemoteTarget
        ? ConnectionState switch
        {
            ConnectionState.Connecting => "Remote · connecting…",
            ConnectionState.Failed => "Remote · not connected",
            _ => CanWrite ? "Remote · changes enabled" : "Remote · read-only",
        }
        : CanWrite ? "Administrator · changes enabled" : "Read-only · preview only";

    public string ApplyToolTip => CanWrite
        ? IsRemoteTarget
            ? $"Write the pending changes to the registry of {TargetName}. A .reg backup of its current settings is saved on this PC first."
            : "Write the pending changes to the registry. A .reg backup of the current settings is saved first."
        : _target is null
            ? $"Not connected to {TargetName}."
            : IsRemoteTarget
                ? $"{ReadOnlyReason} Start CryptoFixer without --read-only to apply changes to remote machines."
                : $"{ReadOnlyReason} Run CryptoFixer as Administrator to apply changes.";

    public string BackupFolder { get; }

    public string OsSummary => _target?.Os.Summary ?? (IsConnecting ? "Connecting…" : "Not connected");

    public string DefaultsSummary => _target is { } target
        ? $"OS defaults shown for {OsDefaults.DescribeDefaultsFamily(target.Os.Build)}"
        : _currentMachine.Details;

    /// <summary>The registry folder of the selected section, e.g. HKEY_LOCAL_MACHINE\...\SCHANNEL\Ciphers.</summary>
    public string RegistryPath => $@"{SchannelRegistryPath}\{SectionFolder}";

    private string SchannelRegistryPath => _target?.RegistryPath ?? $@"\\{_currentMachine.Host}\HKEY_LOCAL_MACHINE\{SchannelPaths.Schannel}";

    private string SectionFolder => CurrentCategory is { } category ? AlgorithmCatalog.FolderName(category) : "Protocols";

    // ---- Machine shown ----

    public bool IsRemoteTarget => _currentMachine.IsRemote;

    public bool HasTarget => _target is not null;

    /// <summary>"This computer" or the remote machine's display name.</summary>
    public string TargetName => _currentMachine.Name;

    public string TargetDetail => IsRemoteTarget ? _currentMachine.Details : _localTarget.HostName;

    /// <summary>Names the computer in backups, presets and dialogs, e.g. "web01 (10.0.0.5)".</summary>
    private string TargetLabel => _target is null || _target.IsLocal
        ? _localTarget.HostName
        : string.Equals(TargetName, _target.HostName, StringComparison.OrdinalIgnoreCase) ? _target.HostName : $"{TargetName} ({_target.HostName})";

    public ConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (SetProperty(ref _connectionState, value))
            {
                OnPropertyChanged(nameof(IsConnecting));
                OnPropertyChanged(nameof(IsConnectionFailed));
                OnPropertyChanged(nameof(ShowOverlay));
            }
        }
    }

    public bool IsConnecting => ConnectionState == ConnectionState.Connecting;

    public bool IsConnectionFailed => ConnectionState == ConnectionState.Failed;

    /// <summary>Heading of the table overlay: "Connecting", "Couldn't connect", "Connection lost" or "Restarting".</summary>
    public string ConnectionTitle
    {
        get => _connectionTitle;
        private set => SetProperty(ref _connectionTitle, value);
    }

    public string ConnectionMessage
    {
        get => _connectionMessage;
        private set => SetProperty(ref _connectionMessage, value);
    }

    /// <summary>Technical detail of a failed connection (error code and original text).</summary>
    public string? ConnectionDetail
    {
        get => _connectionDetail;
        private set
        {
            if (SetProperty(ref _connectionDetail, value))
            {
                OnPropertyChanged(nameof(HasConnectionDetail));
            }
        }
    }

    public bool HasConnectionDetail => !string.IsNullOrEmpty(ConnectionDetail);

    /// <summary>A remote read, write, backup or restart is running.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(ShowOverlay));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    /// <summary>Covers the protocol table while connecting, after a failed connection, or during a remote operation.</summary>
    public bool ShowOverlay => IsBusy || ConnectionState != ConnectionState.Ready;

    public ICommand RetryConnectCommand { get; }

    public ICommand EditCurrentMachineCommand { get; }

    /// <summary>Also cancels a connection attempt.</summary>
    public ICommand BackToLocalCommand { get; }

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

    /// <summary>Changes were applied to the computer shown and it hasn't been restarted from CryptoFixer since (tracked per computer).</summary>
    public bool RestartRequired => _restartPending.Contains(TargetKey);

    private string TargetKey => IsRemoteTarget ? _currentMachine.Host.Trim() : LocalKey;

    public string RestartNotice => RestartRequired
        ? IsRemoteTarget
            ? $"Changes were written to the registry of {TargetName}. Restart {TargetName} to activate them."
            : "Changes were written to the registry. Restart Windows to activate them (restarting affected services alone is not sufficient for all applications)."
        : IsRemoteTarget
            ? $"Schannel reads these settings when Windows starts. Changes take effect only after {TargetName} is restarted."
            : "Schannel reads these settings when Windows starts. Any change takes effect only after Windows is restarted.";

    public string RestartButtonText => IsRemoteTarget ? $"Restart {TargetName}…" : "Restart Windows…";

    private void MarkRestartRequired(bool required)
    {
        if (required)
        {
            _restartPending.Add(TargetKey);
        }
        else
        {
            _restartPending.Remove(TargetKey);
        }

        OnPropertyChanged(nameof(RestartRequired));
        OnPropertyChanged(nameof(RestartNotice));
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- Sections ----

    /// <summary>Protocols, Ciphers, Hashes and Key exchange; each label shows its number of pending changes.</summary>
    public IReadOnlyList<SectionOption> Sections { get; }

    public SettingsSection Section
    {
        get => _section;
        set
        {
            if (!Enum.IsDefined(value) || !SetProperty(ref _section, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsProtocolSection));
            OnPropertyChanged(nameof(IsAlgorithmSection));
            OnPropertyChanged(nameof(SectionNoun));
            OnPropertyChanged(nameof(SearchPlaceholder));
            OnPropertyChanged(nameof(RegistryPath));
            OnPropertyChanged(nameof(SortDirectionGlyph));
            OnPropertyChanged(nameof(SortDirectionToolTip));
            OnPropertyChanged(nameof(SectionHint));
            RaiseLayoutVisibility();
            ApplyView();
        }
    }

    public bool IsProtocolSection => Section == SettingsSection.Protocols;

    public bool IsAlgorithmSection => !IsProtocolSection;

    /// <summary>The algorithm category shown, or null for the protocols section.</summary>
    public AlgorithmCategory? CurrentCategory => Section switch
    {
        SettingsSection.Ciphers => AlgorithmCategory.Cipher,
        SettingsSection.Hashes => AlgorithmCategory.Hash,
        SettingsSection.KeyExchange => AlgorithmCategory.KeyExchange,
        _ => null,
    };

    /// <summary>Plural noun for counts and empty-state text, e.g. "ciphers".</summary>
    public string SectionNoun => Section switch
    {
        SettingsSection.Ciphers => "ciphers",
        SettingsSection.Hashes => "hashes",
        SettingsSection.KeyExchange => "key exchange algorithms",
        _ => "protocols",
    };

    public string SearchPlaceholder => $"Search {SectionNoun}";

    /// <summary>One-line explanation shown above the algorithm table.</summary>
    public string SectionHint => Section switch
    {
        SettingsSection.Ciphers =>
            "Enabled = 0xffffffff allows a cipher, 0 blocks it everywhere in Schannel. A cipher is only used if a suite in the cipher suite list uses it. * = default inferred from Microsoft's suite tables.",
        SettingsSection.Hashes =>
            "Blocking a hash disables every cipher suite that uses it for message authentication. * = Microsoft doesn't document this key.",
        SettingsSection.KeyExchange =>
            "Blocking a key exchange disables every cipher suite that uses it. Diffie-Hellman and PKCS key sizes are shown read-only. * = undocumented key.",
        _ => string.Empty,
    };

    // ---- Filtering and sorting ----

    public IReadOnlyList<Choice<ProtocolStateFilter>> StateFilterOptions { get; } =
    [
        new(ProtocolStateFilter.All, "All"),
        new(ProtocolStateFilter.Enabled, "Enabled"),
        new(ProtocolStateFilter.Disabled, "Disabled"),
        new(ProtocolStateFilter.NotSupported, "Not supported"),
        new(ProtocolStateFilter.SetInRegistry, "Set in registry"),
        new(ProtocolStateFilter.OsDefault, "Using OS default"),
        new(ProtocolStateFilter.PendingChanges, "Pending changes"),
        new(ProtocolStateFilter.Deprecated, "Deprecated"),
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

    public IReadOnlyList<Choice<AlgorithmSortKey>> AlgorithmSortOptions { get; } =
    [
        new(AlgorithmSortKey.Strength, "Strength (weakest first)"),
        new(AlgorithmSortKey.Name, "Name"),
        new(AlgorithmSortKey.State, "State"),
        new(AlgorithmSortKey.Source, "Set in registry first"),
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

    public AlgorithmSortKey AlgorithmSortKey
    {
        get => _algorithmSortKey;
        set => SetAlgorithmSort(value, AlgorithmSortDescending);
    }

    public bool AlgorithmSortDescending
    {
        get => _algorithmSortDescending;
        set => SetAlgorithmSort(AlgorithmSortKey, value);
    }

    private bool CurrentSortDescending => IsProtocolSection ? SortDescending : AlgorithmSortDescending;

    public string SortDirectionGlyph => CurrentSortDescending ? "↓" : "↑";

    public string SortDirectionToolTip => CurrentSortDescending ? "Descending (click for ascending)" : "Ascending (click for descending)";

    public bool ShowServerColumn => RoleScope != RoleScope.Client;

    public bool ShowClientColumn => RoleScope != RoleScope.Server;

    /// <summary>A filter that affects the section shown (the Side filter only applies to protocols).</summary>
    public bool IsFilterActive => IsProtocolSection
        ? _filter.IsActive
        : !string.IsNullOrWhiteSpace(SearchText) || StateFilter != ProtocolStateFilter.All;

    private int SectionTotal => CurrentCategory is { } category ? AlgorithmRows.Count(r => r.Category == category) : Rows.Count;

    private int SectionVisible => IsProtocolSection ? _visibleCount : _algorithmVisibleCount;

    public bool HasNoVisibleRows => SectionTotal > 0 && SectionVisible == 0;

    public string VisibleCountText => IsFilterActive
        ? $"Showing {SectionVisible} of {SectionTotal} {SectionNoun}"
        : $"{SectionTotal} {SectionNoun}";

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
            RaiseLayoutVisibility();
            SaveSettings();
        }
    }

    public bool IsListMode => DisplayMode == TableDisplayMode.List;

    public bool IsTableMode => !IsListMode;

    public bool ShowProtocolTable => IsProtocolSection && IsTableMode;

    public bool ShowProtocolList => IsProtocolSection && IsListMode;

    public bool ShowAlgorithmTable => IsAlgorithmSection && IsTableMode;

    public bool ShowAlgorithmList => IsAlgorithmSection && IsListMode;

    private void RaiseLayoutVisibility()
    {
        OnPropertyChanged(nameof(ShowProtocolTable));
        OnPropertyChanged(nameof(ShowProtocolList));
        OnPropertyChanged(nameof(ShowAlgorithmTable));
        OnPropertyChanged(nameof(ShowAlgorithmList));
    }

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

    /// <summary>Built-in presets for the computer shown (e.g. TLS 1.3 only where its OS supports it).</summary>
    public IReadOnlyList<ProtocolPreset> BuiltInPresetList
    {
        get => _builtInPresets;
        private set => SetProperty(ref _builtInPresets, value);
    }

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

    public IReadOnlyList<AlgorithmChange> GetAlgorithmChanges() =>
        AlgorithmRows.Select(r => r.Cell.ToChange()).OfType<AlgorithmChange>().ToList();

    /// <summary>Returns false if the user cancelled closing because of pending changes.</summary>
    public bool ConfirmClose() =>
        _closingForUpdate || !CanWrite || !HasPendingChanges ||
        _dialogs.Confirm("Discard pending changes?", $"You have {PendingSummary.ToLowerInvariant()} that were not applied. Exit anyway?");

    /// <summary>Closes the remote connection when the window closes.</summary>
    public void Shutdown()
    {
        _switchGeneration++;
        _connectCancellation?.Cancel();
        if (_target is { IsLocal: false } remote)
        {
            _target = null;
            remote.Dispose();
        }
    }

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

    /// <summary>Algorithm column header click: ascending, then descending, then back to strength order.</summary>
    public void SortAlgorithmsByColumn(AlgorithmSortKey key)
    {
        if (AlgorithmSortKey != key)
        {
            SetAlgorithmSort(key, descending: false);
        }
        else if (!AlgorithmSortDescending)
        {
            SetAlgorithmSort(key, descending: true);
        }
        else
        {
            SetAlgorithmSort(AlgorithmSortKey.Strength, descending: false);
        }
    }

    private void SetAlgorithmSort(AlgorithmSortKey key, bool descending)
    {
        if (key == _algorithmSortKey && descending == _algorithmSortDescending)
        {
            return;
        }

        _algorithmSortKey = key;
        _algorithmSortDescending = descending;
        ApplyView();
        OnPropertyChanged(nameof(AlgorithmSortKey));
        OnPropertyChanged(nameof(AlgorithmSortDescending));
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

        using (AlgorithmRowsView.DeferRefresh())
        {
            AlgorithmRowsView.CustomSort = new AlgorithmComparer(new AlgorithmRowComparer(AlgorithmSortKey, AlgorithmSortDescending));
        }

        OnPropertyChanged(nameof(IsFilterActive));
        UpdateVisibleCount();
    }

    private void UpdateVisibleCount()
    {
        _visibleCount = RowsView.Count;
        _algorithmVisibleCount = AlgorithmRowsView?.Count ?? 0;
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

    private void PopulateRows(SchannelSnapshot snapshot)
    {
        _snapshot = snapshot;
        var states = snapshot.Protocols;
        Rows.Clear();
        foreach (var protocol in ProtocolCatalog.All)
        {
            var server = states.Single(s => s.Protocol == protocol && s.Role == ProtocolRole.Server);
            var client = states.Single(s => s.Protocol == protocol && s.Role == ProtocolRole.Client);
            Rows.Add(new ProtocolRowViewModel(
                protocol,
                new ProtocolCellViewModel(server, OnCellChanged),
                new ProtocolCellViewModel(client, OnCellChanged)));
        }

        AlgorithmRows.Clear();
        foreach (var state in snapshot.Algorithms)
        {
            AlgorithmRows.Add(new AlgorithmRowViewModel(new AlgorithmCellViewModel(state, OnCellChanged), snapshot.CipherSuites));
        }

        BuiltInPresetList = BuiltInPresets.For(states);
        OnCellChanged();
        UpdateVisibleCount();
    }

    private void ClearRows()
    {
        _snapshot = EmptySnapshot;
        Rows.Clear();
        AlgorithmRows.Clear();
        BuiltInPresetList = [];
        OnCellChanged();
        UpdateVisibleCount();
    }

    private void OnCellChanged()
    {
        var protocolChanges = GetPendingChanges().Count;
        var algorithmChanges = GetAlgorithmChanges();
        foreach (var section in Sections)
        {
            section.PendingCount = section.Value switch
            {
                SettingsSection.Protocols => protocolChanges,
                SettingsSection.Ciphers => algorithmChanges.Count(c => c.Algorithm.Category == AlgorithmCategory.Cipher),
                SettingsSection.Hashes => algorithmChanges.Count(c => c.Algorithm.Category == AlgorithmCategory.Hash),
                _ => algorithmChanges.Count(c => c.Algorithm.Category == AlgorithmCategory.KeyExchange),
            };
        }

        PendingCount = protocolChanges + algorithmChanges.Count;
        CommandManager.InvalidateRequerySuggested();
    }

    private async void Refresh()
    {
        if (_target is null)
        {
            TrySwitch(_currentMachine, force: true);
            return;
        }

        if (HasPendingChanges && !_dialogs.Confirm("Refresh", "Refreshing discards your pending changes. Continue?"))
        {
            return;
        }

        if (await TryReloadAsync())
        {
            StatusText = IsRemoteTarget
                ? $"Reloaded the settings of {TargetName} at {DateTime.Now:T}."
                : $"Reloaded settings from the registry at {DateTime.Now:T}.";
        }
    }

    private async void Discard()
    {
        if (_target is { IsLocal: false })
        {
            // Restore the values read when connecting or last refreshing, without another round trip.
            PopulateRows(_snapshot);
        }
        else if (!await TryReloadAsync())
        {
            return;
        }

        StatusText = "Pending changes discarded.";
    }

    /// <summary>Re-reads the computer shown. A failed remote read shows the connection error instead of the table.</summary>
    private async Task<bool> TryReloadAsync()
    {
        if (_target is not { } target)
        {
            return false;
        }

        try
        {
            var snapshot = await RunAsync(target, $"Reading the Schannel settings of {TargetName}…", () => target.Store.ReadSnapshot(target.Os.Build));
            if (!ReferenceEquals(target, _target))
            {
                return false;
            }

            PopulateRows(snapshot);
            return true;
        }
        catch (Exception ex) when (!target.IsLocal)
        {
            if (ReferenceEquals(target, _target))
            {
                ShowConnectionFailed(_currentMachine, ex, "Connection lost");
            }

            return false;
        }
    }

    /// <summary>Runs <paramref name="work"/> inline for the local registry, or on a worker thread behind the busy overlay for a remote one.</summary>
    private async Task<T> RunAsync<T>(SchannelTarget target, string busyText, Func<T> work)
    {
        if (target.IsLocal)
        {
            return work();
        }

        BusyText = busyText;
        IsBusy = true;
        try
        {
            return await Task.Run(work);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task RunAsync(SchannelTarget target, string busyText, Action work) =>
        RunAsync(target, busyText, () =>
        {
            work();
            return true;
        });

    // ---- Machines ----

    bool IMachineHost.RequestSwitch(MachineItemViewModel machine) => TrySwitch(machine);

    bool IMachineHost.ReleaseMachine(MachineItemViewModel machine) =>
        !ReferenceEquals(machine, _currentMachine) || TrySwitch(Machines.Local);

    void IMachineHost.MachineEdited(MachineItemViewModel machine, bool connectionChanged)
    {
        if (!ReferenceEquals(machine, _currentMachine))
        {
            return;
        }

        RaiseTargetChanged();
        if (connectionChanged && !TrySwitch(machine, force: true))
        {
            if (_target is not null)
            {
                machine.SetStatus(MachineStatus.Connected, "Still connected with the previous connection settings.");
            }

            StatusText = $"The new connection settings of {machine.Name} are used the next time you connect to it.";
        }
    }

    void IMachineHost.ReportStatus(string message) => StatusText = message;

    /// <summary>Shows <paramref name="machine"/>: asks to discard pending changes and for a password if needed, then connects.</summary>
    /// <param name="force">Reconnect even when the machine is already shown.</param>
    private bool TrySwitch(MachineItemViewModel machine, bool force = false)
    {
        if (IsBusy)
        {
            StatusText = "Wait until the current operation finishes before switching machines.";
            return false;
        }

        if (!force && ReferenceEquals(machine, _currentMachine) && ConnectionState != ConnectionState.Failed)
        {
            return true;
        }

        if (HasPendingChanges)
        {
            var action = ReferenceEquals(machine, _currentMachine) ? "Reconnecting" : $"Switching to {machine.Name}";
            if (!_dialogs.Confirm("Discard pending changes?",
                    $"You have {PendingSummary.ToLowerInvariant()} for {TargetName} that weren't applied. {action} discards them. Continue?"))
            {
                return false;
            }
        }

        SecureString? password = null;
        if (machine.IsRemote && !Machines.TryGetPassword(machine, out password))
        {
            StatusText = $"Not connected to {machine.Name}: no password was entered.";
            return false;
        }

        _ = SwitchToAsync(machine, password);
        return true;
    }

    private async Task SwitchToAsync(MachineItemViewModel machine, SecureString? password)
    {
        var generation = ++_switchGeneration;
        _connectCancellation?.Cancel();
        _connectCancellation = null;

        var previous = _currentMachine;
        if (!ReferenceEquals(previous, machine) && previous.Status is MachineStatus.Connected or MachineStatus.Connecting && previous.IsRemote)
        {
            previous.SetStatus(MachineStatus.Unknown);
        }

        if (_target is { IsLocal: false } previousTarget)
        {
            previousTarget.Dispose();
        }

        _target = null;
        _currentMachine = machine;
        Machines.SetSelected(machine);
        ClearRows();

        if (machine.IsLocal)
        {
            _target = _localTarget;
            ConnectionState = ConnectionState.Ready;
            RaiseTargetChanged();
            PopulateRows(_localTarget.Store.ReadSnapshot(_localTarget.Os.Build));
            StatusText = $"Showing this computer ({_localTarget.HostName}).";
            return;
        }

        var entry = machine.Entry!;
        var readOnly = !_remote.AllowWrites;
        var cancellation = new CancellationTokenSource();
        _connectCancellation = cancellation;
        ConnectionTitle = "Connecting";
        ConnectionMessage = $"Connecting to {entry.Name} ({entry.Host}) over {entry.ProtocolLabel}…";
        ConnectionDetail = null;
        ConnectionState = ConnectionState.Connecting;
        machine.SetStatus(MachineStatus.Connecting);
        RaiseTargetChanged();
        StatusText = ConnectionMessage;

        IRemoteSession session;
        SchannelSnapshot snapshot;
        try
        {
            (session, snapshot) = await Task.Run(() =>
            {
                var opened = _remote.Connector.Connect(entry, password, readOnly, cancellation.Token);
                try
                {
                    return (opened, new SchannelProtocolStore(opened.Registry).ReadSnapshot(opened.Os.Build));
                }
                catch
                {
                    opened.Dispose();
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            cancellation.Dispose();
            if (generation == _switchGeneration)
            {
                _connectCancellation = null;
                ShowConnectionFailed(machine, ex, "Couldn't connect");
            }

            return;
        }

        if (generation != _switchGeneration)
        {
            // The user picked another machine meanwhile.
            session.Dispose();
            cancellation.Dispose();
            return;
        }

        _connectCancellation = null;
        _target = SchannelTarget.Remote(entry, session, cancellation, _remote.AllowWrites, _remote.ReadOnlyReason);
        machine.PasswordRejected = false;
        machine.SetStatus(MachineStatus.Connected, $"Connected at {DateTime.Now:T} · {session.Os.Summary}");
        ConnectionState = ConnectionState.Ready;
        RaiseTargetChanged();
        PopulateRows(snapshot);
        StatusText = CanWrite
            ? $"Connected to {entry.Name} ({entry.Host}). Stage changes, then click Apply changes; nothing is written until you apply."
            : $"Connected to {entry.Name} ({entry.Host}) read-only. {ReadOnlyReason}";
    }

    private void ShowConnectionFailed(MachineItemViewModel machine, Exception ex, string title)
    {
        var failure = ex switch
        {
            RemoteAccessException remote => remote.Failure,
            OperationCanceledException => new RemoteFailure(RemoteFailureKind.Other, "The connection attempt was cancelled.", null),
            _ => new RemoteFailure(RemoteFailureKind.Other, $"Reading the Schannel settings of {machine.Host} failed: {ex.Message}", ex.GetType().FullName),
        };

        if (failure.Kind == RemoteFailureKind.LogonFailure)
        {
            MachinesViewModel.MarkPasswordRejected(machine);
        }

        if (_target is { IsLocal: false } broken)
        {
            _target = null;
            broken.Dispose();
        }

        ClearRows();
        ConnectionTitle = title;
        ConnectionMessage = failure.Message;
        ConnectionDetail = failure.Detail;
        ConnectionState = ConnectionState.Failed;
        machine.SetStatus(MachineStatus.Error, failure.Detail is null ? failure.Message : $"{failure.Message}\n\n{failure.Detail}");
        RaiseTargetChanged();
        StatusText = $"{title} ({machine.Name}, {machine.Host}): {failure.Message}";
    }

    private static readonly string[] TargetProperties =
    [
        nameof(CanWrite), nameof(IsReadOnly), nameof(ReadOnlyReason), nameof(ModeText), nameof(ApplyToolTip), nameof(OsSummary),
        nameof(DefaultsSummary), nameof(RegistryPath), nameof(IsRemoteTarget), nameof(HasTarget), nameof(TargetName), nameof(TargetDetail),
        nameof(RestartRequired), nameof(RestartNotice), nameof(RestartButtonText),
    ];

    private void RaiseTargetChanged()
    {
        foreach (var name in TargetProperties)
        {
            OnPropertyChanged(name);
        }

        CommandManager.InvalidateRequerySuggested();
    }

    // ---- Presets ----

    private void StagePreset(ProtocolPreset preset)
    {
        if (_target is null)
        {
            return;
        }

        var plan = PresetPlanner.Plan(preset, _snapshot.Protocols, _snapshot.Algorithms);
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

        foreach (var target in plan.AlgorithmTargets)
        {
            if (AlgorithmRows.FirstOrDefault(r => r.Algorithm == target.Algorithm) is { } row && row.Cell.Selected != target.Target)
            {
                row.Cell.Selected = target.Target;
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

        var otherSections = Sections.Where(s => s.Value != Section && s.PendingCount > 0).Select(s => s.Title).ToList();
        if (otherSections.Count > 0)
        {
            message.Append($" Pending changes in other sections: {string.Join(", ", otherSections)}.");
        }

        var hidden = IsProtocolSection
            ? Rows.Any(r => r.HasPendingChanges && !_filter.Matches(r.ToView()))
            : AlgorithmRows.Any(r => r.Category == CurrentCategory && r.HasPendingChanges && !_filter.Matches(r.ToView()));
        if (IsFilterActive && hidden)
        {
            message.Append(" Some rows with pending changes are hidden by the current filter.");
        }

        if (HasPendingChanges)
        {
            message.Append(CanWrite
                ? " Review and click Apply changes."
                : IsRemoteTarget ? " Preview only: the connection is read-only." : " Preview only: run as Administrator to apply.");
        }

        StatusText = message.ToString();
    }

    private void SavePreset()
    {
        if (_target is not { } target)
        {
            return;
        }

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

        var suggestedName = target.IsLocal ? target.HostName : TargetName;
        var details = _dialogs.AskPresetDetails($"{suggestedName} {DateTime.Now:yyyy-MM-dd}", PresetsPath, PresetFile.ValidateName);
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
        var algorithms = AlgorithmRows.Select(r => new AlgorithmPresetEntry(r.Algorithm, r.Cell.Selected));
        var preset = ProtocolPreset.Capture(details.Name, details.Description, settings, algorithms, DateTimeOffset.Now, $"{TargetLabel}, {target.Os.Summary}", out var skipped);

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

        StatusText = $"Saved preset \"{preset.Name}\" ({preset.Entries.Count} protocol and {preset.Algorithms.Count} cipher, hash and key exchange settings) to {PresetsPath}." +
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

    private async void Apply()
    {
        var protocolChanges = GetPendingChanges();
        var algorithmChanges = GetAlgorithmChanges();
        var count = protocolChanges.Count + algorithmChanges.Count;
        if (count == 0 || !CanWrite || _target is not { } target)
        {
            return;
        }

        var remote = !target.IsLocal;
        var label = TargetLabel;
        var warnings = ChangeAdvisor.GetWarnings(_snapshot.Protocols, protocolChanges)
            .Concat(ChangeAdvisor.GetWarnings(_snapshot.Algorithms, algorithmChanges))
            .ToList();
        var message = new StringBuilder();
        message.AppendLine($"Write {count} change(s) to {target.RegistryPath}?");
        message.AppendLine();
        foreach (var change in protocolChanges.Select(c => c.ToString()).Concat(algorithmChanges.Select(c => c.ToString())))
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
        message.AppendLine(remote
            ? $"A backup of the current settings of {label} will first be saved on this PC to:\n{BackupFolder}"
            : $"A backup of the current settings will first be saved to:\n{BackupFolder}");
        message.AppendLine();
        message.Append(remote
            ? $"{TargetName} must be restarted for the changes to take effect."
            : "Windows must be restarted for the changes to take effect.");

        if (!_dialogs.Confirm("Apply changes", message.ToString(), isWarning: warnings.Count > 0))
        {
            return;
        }

        string backupPath;
        try
        {
            var path = Path.Combine(BackupFolder, DefaultBackupFileName(target));
            backupPath = await RunAsync(target, $"Backing up the settings of {TargetName}…", () => WriteBackup(target, label, path));
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Backup failed", $"Could not save the registry backup, so nothing was changed.\n\n{ex.Message}");
            return;
        }

        try
        {
            await RunAsync(target, $"Writing {count} change(s) to {TargetName}…", () =>
            {
                target.Store.Apply(protocolChanges);
                target.Store.Apply(algorithmChanges);
            });
        }
        catch (Exception ex)
        {
            await TryReloadAsync();
            StatusText = $"Applying failed. Backup: {backupPath}";
            _dialogs.ShowError("Apply failed",
                $"Writing to the registry{(remote ? $" of {label}" : string.Empty)} failed: {ex.Message}\n\nSome changes may already have been written. " +
                $"The previous settings can be restored from:\n{backupPath}" +
                (remote ? $"\n\nImport that file on {label} itself; importing it on this PC would change this PC." : string.Empty));
            return;
        }

        var reloaded = await TryReloadAsync();
        MarkRestartRequired(true);
        StatusText = remote
            ? $"Applied {count} change(s) to {label}. Backup saved to {backupPath}. Restart {TargetName} to activate the changes."
            : $"Applied {count} change(s). Backup saved to {backupPath}. Restart Windows to activate the changes.";

        if (!reloaded || !ReferenceEquals(target, _target))
        {
            return;
        }

        if (remote)
        {
            if (_dialogs.Confirm("Restart required",
                    $"{count} change(s) were applied to {label}.\nBackup (on this PC): {backupPath}\n\n" +
                    $"Schannel only picks up these changes after a restart. Restart {label} now?\n\n" +
                    "It restarts about 10 seconds after you confirm; users and services on it are disconnected."))
            {
                await RestartRemoteAsync(target);
            }
        }
        else if (_dialogs.Confirm("Restart required",
                     $"{count} change(s) were applied.\nBackup: {backupPath}\n\n" +
                     "Schannel only picks up these changes after a restart. Restart Windows now?"))
        {
            _system.RestartComputer();
        }
    }

    private async void ExportBackup()
    {
        if (_target is not { } target)
        {
            return;
        }

        var path = _dialogs.PickSaveFile(BackupFolder, DefaultBackupFileName(target));
        if (path is null)
        {
            return;
        }

        var label = TargetLabel;
        try
        {
            await RunAsync(target, $"Exporting the settings of {TargetName}…", () => WriteBackup(target, label, path));
            StatusText = $"Backup exported to {path}.";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Export failed", ex.Message);
        }
    }

    private async void Restart()
    {
        if (_target is not { } target || !CanWrite)
        {
            return;
        }

        if (target.IsLocal)
        {
            if (_dialogs.Confirm("Restart Windows", "Restart Windows now to activate the Schannel changes? Save your work in other applications first.", isWarning: true))
            {
                _system.RestartComputer();
            }

            return;
        }

        if (_dialogs.Confirm($"Restart {TargetName}",
                $"Restart {TargetLabel} now to activate the Schannel changes?\n\n" +
                "It restarts about 10 seconds after you confirm. Users and services on it are disconnected, and unsaved work there is lost.",
                isWarning: true))
        {
            await RestartRemoteAsync(target);
        }
    }

    private async Task RestartRemoteAsync(SchannelTarget target)
    {
        var machine = _currentMachine;
        var label = TargetLabel;
        try
        {
            await RunAsync(target, $"Asking {machine.Name} to restart…", () => target.Session!.Restart(RemoteRestartComment));
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Restart failed", $"{label} couldn't be restarted: {ex.Message}");
            return;
        }

        MarkRestartRequired(false);
        StatusText = $"Restart requested for {label} at {DateTime.Now:T}.";
        if (!ReferenceEquals(target, _target))
        {
            return;
        }

        // The connection drops while it restarts; reconnect later to check the result.
        _target = null;
        target.Dispose();
        ClearRows();
        ConnectionTitle = "Restarting";
        ConnectionMessage = $"{machine.Name} is restarting. Click Try again once it's back online to check the new settings.";
        ConnectionDetail = null;
        ConnectionState = ConnectionState.Failed;
        machine.SetStatus(MachineStatus.Unknown);
        RaiseTargetChanged();
    }

    private static string WriteBackup(SchannelTarget target, string label, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var content = RegFileExporter.Export(target.Store.Registry, DateTimeOffset.Now, label, isRemote: !target.IsLocal);
        RegFileExporter.WriteFile(path, content);
        return path;
    }

    private static string DefaultBackupFileName(SchannelTarget target) =>
        $"Schannel-{target.FileNameHost}-{DateTime.Now:yyyyMMdd-HHmmss}.reg";

    private sealed class RowComparer(ProtocolRowComparer inner) : IComparer
    {
        public int Compare(object? x, object? y) =>
            inner.Compare((x as ProtocolRowViewModel)?.ToView(), (y as ProtocolRowViewModel)?.ToView());
    }

    private sealed class AlgorithmComparer(AlgorithmRowComparer inner) : IComparer
    {
        public int Compare(object? x, object? y) =>
            inner.Compare((x as AlgorithmRowViewModel)?.ToView(), (y as AlgorithmRowViewModel)?.ToView());
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

        foreach (var category in preset.Algorithms.GroupBy(a => a.Algorithm.Category))
        {
            // Compact: "Ciphers: disabled NULL, DES 56/56, …; enabled AES 128/128, AES 256/256".
            var parts = category
                .GroupBy(a => a.Setting)
                .Select(g => $"{Label(g.Key)} {string.Join(", ", g.Select(a => a.Algorithm.KeyName))}");
            text.AppendLine($"{AlgorithmCatalog.Title(category.Key)}: {string.Join("; ", parts)}");
        }

        if (preset.Entries.Count == 0 && preset.Algorithms.Count == 0)
        {
            text.AppendLine("(no settings)");
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

        text.AppendLine().AppendLine().Append("Loading only stages these settings; anything not listed is left unchanged.");
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
