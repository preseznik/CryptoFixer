using System.ComponentModel;
using System.Text;
using System.Windows.Input;
using CryptoFixer.Core.Schannel;

namespace CryptoFixer.ViewModels;

/// <summary>The part of SCHANNEL shown in the table.</summary>
public enum SettingsSection
{
    Protocols,
    Ciphers,
    Hashes,
    KeyExchange,
}

/// <summary>An entry of the section switcher; the label shows the number of pending changes in that section.</summary>
public sealed class SectionOption : ObservableObject
{
    private int _pendingCount;

    public SectionOption(SettingsSection value, string title, string toolTip)
    {
        Value = value;
        Title = title;
        ToolTip = toolTip;
    }

    public SettingsSection Value { get; }

    public string Title { get; }

    public string ToolTip { get; }

    public int PendingCount
    {
        get => _pendingCount;
        set
        {
            if (SetProperty(ref _pendingCount, value))
            {
                OnPropertyChanged(nameof(Label));
            }
        }
    }

    public string Label => PendingCount > 0 ? $"{Title}  •{PendingCount}" : Title;

    public override string ToString() => Title;
}

/// <summary>
/// The Enabled setting of one cipher, hash or key exchange algorithm. It exposes the same properties as
/// <see cref="ProtocolCellViewModel"/> so the cell templates of all display modes can be shared.
/// </summary>
public sealed class AlgorithmCellViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private ConfiguredSetting _selected;

    public AlgorithmCellViewModel(AlgorithmState state, Action onChanged)
    {
        State = state;
        _onChanged = onChanged;
        _selected = state.Configured;
        Options =
        [
            new(ConfiguredSetting.NotConfigured, $"OS default ({ProtocolStateEvaluator.Describe(state.OsDefault.State)}{(state.OsDefault.IsInferred ? "*" : string.Empty)})"),
            new(ConfiguredSetting.Enabled, "Enabled"),
            new(ConfiguredSetting.Disabled, "Disabled"),
        ];
    }

    public AlgorithmState State { get; }

    public IReadOnlyList<SettingOption> Options { get; }

    public ConfiguredSetting Selected
    {
        get => _selected;
        set
        {
            if (Options.All(o => o.Value != value) || !SetProperty(ref _selected, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(PendingText));
            OnPropertyChanged(nameof(ShortPendingText));
            _onChanged();
        }
    }

    public bool IsDirty => Selected != State.Configured;

    /// <summary>The state after applying the selected setting.</summary>
    public AlgorithmState Projected => State.With(Selected);

    public string RoleLabel => "State";

    public string AccessibleName => $"{State.Algorithm.DisplayName} setting";

    public string EffectiveText => ProtocolStateEvaluator.Describe(State.Effective);

    public string SourceText => State.IsExplicitlyConfigured
        ? "Set in registry"
        : State.OsDefault.IsInferred ? "OS default (inferred)" : "OS default (not configured)";

    public string ShortSourceText => State.IsExplicitlyConfigured
        ? "· set in registry"
        : State.OsDefault.IsInferred ? "· OS default*" : "· OS default";

    public string PendingText => IsDirty ? $"Pending: {ProtocolStateEvaluator.Describe(Projected.Effective)} after restart" : string.Empty;

    public string ShortPendingText => IsDirty ? $"→ {ProtocolStateEvaluator.Describe(Projected.Effective)}" : string.Empty;

    public StatusTone Tone => State.Effective switch
    {
        EffectiveState.Enabled when State.Algorithm.IsDeprecated => StatusTone.Warning,
        EffectiveState.Disabled when !State.Algorithm.IsDeprecated => StatusTone.Attention,
        _ => StatusTone.Good,
    };

    public string ToolTip
    {
        get
        {
            var text = new StringBuilder();
            text.Append($"HKLM\\{State.KeyPath}\n");
            text.Append($"Enabled = {(State.Enabled is { } v ? $"0x{v:X8} ({v})" : "(not set)")}\n");
            text.Append($"OS default on this build: {ProtocolStateEvaluator.Describe(State.OsDefault.State)}");
            text.Append(State.OsDefault.IsInferred ? " (inferred, not stated by Microsoft)\n" : "\n");
            text.Append(State.OsDefault.Note);
            foreach (var size in State.KeySizes)
            {
                text.Append('\n').Append(size.Describe());
            }

            return text.ToString();
        }
    }

    public AlgorithmChange? ToChange() => IsDirty ? new AlgorithmChange(State.Algorithm, Selected) : null;
}

public sealed class AlgorithmRowViewModel : ObservableObject
{
    private readonly ActiveCipherSuites _suites;
    private readonly IReadOnlyList<string> _listed;

    public AlgorithmRowViewModel(AlgorithmCellViewModel cell, ActiveCipherSuites suites)
    {
        Cell = cell;
        _suites = suites;
        _listed = suites.Using(cell.State.Algorithm);
        ResetToOsDefaultCommand = new RelayCommand(() => Cell.Selected = ConfiguredSetting.NotConfigured);
        Cell.PropertyChanged += OnCellPropertyChanged;
    }

    public AlgorithmCellViewModel Cell { get; }

    public SchannelAlgorithm Algorithm => Cell.State.Algorithm;

    public AlgorithmCategory Category => Algorithm.Category;

    public string Name => Algorithm.DisplayName;

    public override string ToString() => Name;

    public string Description => Algorithm.Description;

    public bool IsDeprecated => Algorithm.IsDeprecated;

    /// <summary>Microsoft doesn't document this key (AES, SHA-2, ECDH).</summary>
    public bool IsUndocumented => !Algorithm.IsDocumented;

    /// <summary>Read-only key sizes of Diffie-Hellman and PKCS, e.g. "ServerMinKeyBitLength: 2048 bits (OS default)".</summary>
    public string KeySizeText => string.Join("  ·  ", Cell.State.KeySizes.Select(k => k.Describe()));

    public bool HasKeySizes => Cell.State.KeySizes.Count > 0;

    public bool HasPendingChanges => Cell.IsDirty;

    public ICommand ResetToOsDefaultCommand { get; }

    public int ListedSuiteCount => _listed.Count;

    /// <summary>The setting (as staged) blocks suites that are still in the cipher suite list.</summary>
    public bool BlocksListedSuites => _listed.Count > 0 && Cell.Projected.Effective == EffectiveState.Disabled;

    /// <summary>A deprecated algorithm that isn't blocked and is used by listed suites.</summary>
    public bool WeakSuitesListed => _listed.Count > 0 && IsDeprecated && Cell.Projected.Effective != EffectiveState.Disabled;

    public string SuitesText => !_suites.IsAvailable
        ? "—"
        : _listed.Count == 0
            ? "none listed"
            : BlocksListedSuites
                ? $"{_listed.Count} listed · blocked"
                : _listed.Count == 1 ? "1 suite" : $"{_listed.Count} suites";

    public string SuitesToolTip
    {
        get
        {
            if (!_suites.IsAvailable)
            {
                return "The cipher suite list couldn't be read.";
            }

            var text = new StringBuilder();
            if (_listed.Count == 0)
            {
                text.Append($"No suite in the {_suites.SourceText} uses {Name}.");
                if (Cell.Projected.Effective == EffectiveState.Enabled)
                {
                    text.Append(" Enabling the key alone doesn't make it usable: a suite using it must also be in the list.");
                }
            }
            else
            {
                text.Append($"{_listed.Count} suite(s) in the {_suites.SourceText} use {Name}");
                text.Append(BlocksListedSuites ? "; this setting blocks them:" : ":");
                foreach (var suite in _listed.Take(25))
                {
                    text.Append("\n  ").Append(suite).Append(CipherSuiteMap.IsTls13(suite) ? "  (TLS 1.3; effect of this key undocumented)" : string.Empty);
                }

                if (_listed.Count > 25)
                {
                    text.Append($"\n  … and {_listed.Count - 25} more");
                }
            }

            text.Append("\n\nThe suite list is read-only here. Change it with Group Policy (SSL Cipher Suite Order) or Enable-/Disable-TlsCipherSuite.");
            return text.ToString();
        }
    }

    public AlgorithmRowView ToView() => new(Cell.State, Cell.IsDirty);

    private void OnCellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlgorithmCellViewModel.IsDirty))
        {
            OnPropertyChanged(nameof(HasPendingChanges));
            OnPropertyChanged(nameof(BlocksListedSuites));
            OnPropertyChanged(nameof(WeakSuitesListed));
            OnPropertyChanged(nameof(SuitesText));
            OnPropertyChanged(nameof(SuitesToolTip));
        }
    }
}
