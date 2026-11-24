using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Windows.Data;
using System.Windows.Input;
using CryptoFixer.Core.Presets;
using CryptoFixer.Core.Remote;
using CryptoFixer.Services;

namespace CryptoFixer.ViewModels;

public enum MachineStatus
{
    Unknown,
    Connecting,
    Connected,
    Error,
}

/// <summary>Callbacks from the machine list to the main view, which owns the connection.</summary>
public interface IMachineHost
{
    /// <summary>The user selected <paramref name="machine"/>. Returns false to keep the current selection (e.g. pending changes weren't discarded).</summary>
    bool RequestSwitch(MachineItemViewModel machine);

    /// <summary>Called before <paramref name="machine"/> is removed. Returns false when it can't be released (it stays in the list).</summary>
    bool ReleaseMachine(MachineItemViewModel machine);

    void MachineEdited(MachineItemViewModel machine, bool connectionChanged);

    void ReportStatus(string message);
}

/// <summary>One entry of the machine panel: "This computer" or a remote machine.</summary>
public sealed class MachineItemViewModel : ObservableObject
{
    private MachineEntry? _entry;
    private MachineStatus _status;
    private string? _statusDetail;

    private MachineItemViewModel(MachineEntry? entry, string localName)
    {
        _entry = entry;
        LocalName = localName;
    }

    public static MachineItemViewModel ThisComputer(string computerName) =>
        new(null, computerName) { _status = MachineStatus.Connected, _statusDetail = "Local registry" };

    public static MachineItemViewModel Remote(MachineEntry entry) => new(entry, string.Empty);

    public bool IsLocal => _entry is null;

    public bool IsRemote => !IsLocal;

    private string LocalName { get; }

    /// <summary>The saved entry; null for "This computer".</summary>
    public MachineEntry? Entry
    {
        get => _entry;
        set
        {
            if (IsLocal || value is null)
            {
                return;
            }

            _entry = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Host));
            OnPropertyChanged(nameof(Details));
            OnPropertyChanged(nameof(StatusToolTip));
        }
    }

    public string Name => _entry?.Name ?? MachineListFile.ThisComputerName;

    public string Host => _entry?.Host ?? LocalName;

    public string Details => _entry is { } e
        ? $"{e.Host} · {e.ProtocolLabel} · {e.Credential.Summary}"
        : $"{LocalName} · local registry";

    public MachineStatus Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string StatusToolTip => Status switch
    {
        MachineStatus.Connecting => $"Connecting to {Host}…",
        MachineStatus.Connected => _statusDetail ?? "Connected",
        MachineStatus.Error => _statusDetail ?? "Connection failed",
        _ => "Not connected yet. Select it to connect.",
    };

    /// <summary>Password typed this session (or decrypted from the file); kept in memory only and never shown.</summary>
    internal SecureString? SessionPassword { get; set; }

    /// <summary>The last connection attempt failed with a logon failure, so the next one asks for the password instead of reusing it.</summary>
    internal bool PasswordRejected { get; set; }

    public void SetStatus(MachineStatus status, string? detail = null)
    {
        _statusDetail = detail;
        Status = status;
        OnPropertyChanged(nameof(StatusToolTip));
    }
}

/// <summary>The slide-out machine list: add/edit/remove, filter, sort, and the portable machines file.</summary>
public sealed class MachinesViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly ISecretProtector _protector;
    private readonly IMachineHost _host;
    private string _filterText = string.Empty;
    private MachineSortOrder _sortOrder = MachineSortOrder.Name;
    private MachineItemViewModel _selected;
    private bool _isPanelOpen;
    private bool _keepCopyBeforeSaving;
    private int _visibleRemoteCount;

    public MachinesViewModel(string filePath, IDialogService dialogs, ISecretProtector protector, IMachineHost host, string computerName)
    {
        FilePath = filePath;
        _dialogs = dialogs;
        _protector = protector;
        _host = host;

        Local = MachineItemViewModel.ThisComputer(computerName);
        _selected = Local;
        Items.Add(Local);

        ItemsView = new ListCollectionView(Items)
        {
            Filter = item => item is MachineItemViewModel m && (m.IsLocal || MachineListQuery.Matches(m.Name, m.Host, FilterText)),
            CustomSort = new ItemComparer(this),
        };

        TogglePanelCommand = new RelayCommand(() => IsPanelOpen = !IsPanelOpen);
        ClosePanelCommand = new RelayCommand(() => IsPanelOpen = false);
        AddCommand = new RelayCommand(Add);
        EditCommand = new RelayCommand<MachineItemViewModel>(Edit, m => m.IsRemote);
        RemoveCommand = new RelayCommand<MachineItemViewModel>(Remove, m => m.IsRemote);
        ConnectCommand = new RelayCommand<MachineItemViewModel>(Connect);
        ClearFilterCommand = new RelayCommand(() => FilterText = string.Empty);
    }

    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath);

    public ObservableCollection<MachineItemViewModel> Items { get; } = [];

    /// <summary>Filtered and sorted <see cref="Items"/>; "This computer" always stays first.</summary>
    public ListCollectionView ItemsView { get; }

    public MachineItemViewModel Local { get; }

    public IEnumerable<MachineItemViewModel> RemoteMachines => Items.Where(m => m.IsRemote);

    public IReadOnlyList<Choice<MachineSortOrder>> SortOptions { get; } =
    [
        new(MachineSortOrder.Name, "Name"),
        new(MachineSortOrder.Address, "IP address"),
    ];

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value ?? string.Empty))
            {
                RefreshView();
            }
        }
    }

    public MachineSortOrder SortOrder
    {
        get => _sortOrder;
        set
        {
            if (SetProperty(ref _sortOrder, value))
            {
                RefreshView();
            }
        }
    }

    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        set => SetProperty(ref _isPanelOpen, value);
    }

    /// <summary>The machine shown in the main view. Selecting another one asks the host to connect to it.</summary>
    public MachineItemViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            // Null happens when the filter hides the selection; the main view keeps showing that machine.
            if (value is null || ReferenceEquals(value, _selected))
            {
                return;
            }

            if (_host.RequestSwitch(value))
            {
                _selected = value;
                OnPropertyChanged();
            }
            else
            {
                // Let the ListBox finish its selection change first, then move it back.
                SynchronizationContext.Current?.Post(_ => OnPropertyChanged(nameof(SelectedItem)), null);
            }
        }
    }

    public bool IsFilterActive => !string.IsNullOrWhiteSpace(FilterText);

    public string CountText
    {
        get
        {
            var total = Items.Count - 1;
            if (total == 0)
            {
                return "No remote machines yet";
            }

            return IsFilterActive ? $"{_visibleRemoteCount} of {total} remote machines" : total == 1 ? "1 remote machine" : $"{total} remote machines";
        }
    }

    public bool HasNoMatches => IsFilterActive && _visibleRemoteCount == 0 && Items.Count > 1;

    public ICommand TogglePanelCommand { get; }

    public ICommand ClosePanelCommand { get; }

    public ICommand AddCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand ConnectCommand { get; }

    public ICommand ClearFilterCommand { get; }

    /// <summary>Loads the machines file; returns a status message when something needs the user's attention.</summary>
    public string? Load()
    {
        var result = MachineListFile.Load(FilePath);
        foreach (var machine in RemoteMachines.ToList())
        {
            Items.Remove(machine);
        }

        foreach (var entry in result.Machines)
        {
            Items.Add(MachineItemViewModel.Remote(entry));
        }

        _keepCopyBeforeSaving = result.KeepCopyBeforeSaving;
        RefreshView();

        if (result.Error is not null)
        {
            return $"Machine list problem: {result.Error} The file is kept as {FileName}.invalid-<date> when you next change the list. ({FilePath})";
        }

        return result.Warnings.Count > 0
            ? $"Machine list: {string.Join(" ", result.Warnings)} ({FilePath})"
            : null;
    }

    /// <summary>Selects <paramref name="machine"/> without asking the host (the host already switched).</summary>
    public void SetSelected(MachineItemViewModel machine)
    {
        _selected = machine;
        OnPropertyChanged(nameof(SelectedItem));
    }

    /// <summary>
    /// Gets the password to connect with: none for the current Windows account, else the session copy, the saved (DPAPI) one,
    /// or a prompt. Returns false when the user cancels the prompt.
    /// </summary>
    public bool TryGetPassword(MachineItemViewModel machine, out SecureString? password)
    {
        password = null;
        if (machine.Entry is not { } entry || entry.Credential.UsesCurrentAccount)
        {
            return true;
        }

        string? reason = null;
        if (machine.PasswordRejected)
        {
            reason = $"{entry.Host} rejected the user name or password. Enter the password again, or edit the machine to change the user name.";
        }
        else if (machine.SessionPassword is { } cached)
        {
            password = cached;
            return true;
        }
        else if (entry.Credential.ProtectedPassword is { } blob)
        {
            try
            {
                password = _protector.Unprotect(blob);
                machine.SessionPassword = password;
                return true;
            }
            catch (CryptographicException)
            {
                reason = "The saved password can't be decrypted here. Saved passwords only decrypt for the Windows user who saved them, on the same PC. Please enter it again.";
            }
        }

        var answer = _dialogs.AskPassword(entry, reason);
        if (answer is null)
        {
            return false;
        }

        password = answer.Password;
        machine.SessionPassword = password;
        machine.PasswordRejected = false;

        if (answer.SavePassword || reason is not null)
        {
            // Replace the saved password, or drop one that is wrong or can't be decrypted here when the user chose not to save it.
            var newBlob = answer.SavePassword ? TryProtect(password) : null;
            machine.Entry = entry with { Credential = MachineCredential.ForUser(entry.Credential.UserName!, newBlob) };
            Save();
        }

        return true;
    }

    /// <summary>After a logon failure: forget the in-memory password and ask for it on the next attempt.</summary>
    public static void MarkPasswordRejected(MachineItemViewModel machine)
    {
        machine.SessionPassword = null;
        machine.PasswordRejected = !machine.Entry?.Credential.UsesCurrentAccount ?? false;
    }

    private void Add()
    {
        var result = _dialogs.EditMachine(null, FilePath, r => Validate(r, null));
        if (result is null)
        {
            return;
        }

        var (entry, password) = BuildEntry(result, null);
        var machine = MachineItemViewModel.Remote(entry);
        machine.SessionPassword = password;
        Items.Add(machine);
        RefreshView();
        if (Save())
        {
            _host.ReportStatus($"Added {entry.Name} ({entry.Host}). Select it in the machine list to connect.");
        }
    }

    private void Edit(MachineItemViewModel machine)
    {
        if (machine.Entry is not { } old)
        {
            return;
        }

        var result = _dialogs.EditMachine(old, FilePath, r => Validate(r, old.Name));
        if (result is null)
        {
            return;
        }

        var (entry, password) = BuildEntry(result, old);
        var sameAccount = string.Equals(old.Credential.UserName ?? string.Empty, entry.Credential.UserName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var connectionChanged = password is not null || !sameAccount ||
                                !string.Equals(old.Host, entry.Host, StringComparison.OrdinalIgnoreCase) || old.Protocol != entry.Protocol;

        machine.Entry = entry;
        if (password is not null || !sameAccount || entry.Credential.UsesCurrentAccount)
        {
            machine.SessionPassword = password;
            machine.PasswordRejected = false;
        }

        if (connectionChanged)
        {
            machine.SetStatus(MachineStatus.Unknown);
        }

        RefreshView();
        if (Save())
        {
            _host.ReportStatus($"Saved {entry.Name}.");
        }

        _host.MachineEdited(machine, connectionChanged);
    }

    private void Remove(MachineItemViewModel machine)
    {
        if (machine.Entry is not { } entry)
        {
            return;
        }

        var passwordNote = entry.Credential.HasSavedPassword ? " Its saved password is deleted too." : string.Empty;
        if (!_dialogs.Confirm("Remove machine", $"Remove {entry.Name} ({entry.Host}) from the machine list?{passwordNote}") ||
            !_host.ReleaseMachine(machine))
        {
            return;
        }

        Items.Remove(machine);
        machine.SessionPassword = null;
        RefreshView();
        if (Save())
        {
            _host.ReportStatus($"Removed {entry.Name} from the machine list.");
        }
    }

    private void Connect(MachineItemViewModel machine)
    {
        if (_host.RequestSwitch(machine))
        {
            SetSelected(machine);
        }
    }

    private string? Validate(MachineDialogResult result, string? originalName) =>
        MachineEntry.Validate(
            result.Name,
            result.Host,
            result.UserName,
            result.UseCurrentAccount,
            RemoteMachines.Select(m => m.Name),
            originalName);

    private (MachineEntry Entry, SecureString? Password) BuildEntry(MachineDialogResult result, MachineEntry? old)
    {
        var name = result.Name.Trim();
        var host = result.Host.Trim();
        if (result.UseCurrentAccount)
        {
            return (new MachineEntry(name, host, result.Protocol, MachineCredential.CurrentAccount), null);
        }

        var userName = result.UserName!.Trim();
        if (result.Password is { } password)
        {
            var blob = result.SavePassword ? TryProtect(password) : null;
            return (new MachineEntry(name, host, result.Protocol, MachineCredential.ForUser(userName, blob)), password);
        }

        // No new password: keep the saved one only for the same account and when saving is still wanted.
        var keep = result.SavePassword && old is not null &&
                   string.Equals(old.Credential.UserName, userName, StringComparison.OrdinalIgnoreCase);
        return (new MachineEntry(name, host, result.Protocol, MachineCredential.ForUser(userName, keep ? old!.Credential.ProtectedPassword : null)), null);
    }

    private byte[]? TryProtect(SecureString password)
    {
        try
        {
            return _protector.Protect(password);
        }
        catch (CryptographicException ex)
        {
            _dialogs.ShowError("Password not saved", $"Windows couldn't encrypt the password, so it isn't saved: {ex.Message}\n\nIt's kept in memory until CryptoFixer closes.");
            return null;
        }
    }

    private bool Save()
    {
        try
        {
            string? movedTo = null;
            if (_keepCopyBeforeSaving && File.Exists(FilePath))
            {
                movedTo = PresetFile.MoveAside(FilePath, DateTime.Now);
            }

            MachineListFile.Save(FilePath, RemoteMachines.Select(m => m.Entry!));
            _keepCopyBeforeSaving = false;
            if (movedTo is not null)
            {
                _host.ReportStatus($"The previous machine list couldn't be read completely; it was kept as {movedTo}.");
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Couldn't save the machine list",
                $"Writing {FilePath} failed: {ex.Message}\n\n" +
                "The machine list is stored next to CryptoFixer.exe so it travels with it. If the app is in a protected folder such as " +
                "Program Files, run it as Administrator or copy it to a writable folder. Your changes are kept until CryptoFixer closes.");
            return false;
        }
    }

    private void RefreshView()
    {
        ItemsView.Refresh();
        _visibleRemoteCount = ItemsView.Cast<MachineItemViewModel>().Count(m => m.IsRemote);
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasNoMatches));

        // Re-select the current machine if the filter hid it and now shows it again.
        OnPropertyChanged(nameof(SelectedItem));
    }

    private sealed class ItemComparer(MachinesViewModel owner) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not MachineItemViewModel a || y is not MachineItemViewModel b)
            {
                return 0;
            }

            if (a.IsLocal || b.IsLocal)
            {
                return a.IsLocal == b.IsLocal ? 0 : a.IsLocal ? -1 : 1;
            }

            return MachineListQuery.Comparer(owner.SortOrder).Compare(a.Entry!, b.Entry!);
        }
    }
}
