using System.ComponentModel;
using System.Windows.Input;
using CryptoFixer.Core.Schannel;

namespace CryptoFixer.ViewModels;

public enum StatusTone
{
    /// <summary>Modern protocol enabled, or deprecated protocol disabled.</summary>
    Good,

    /// <summary>Deprecated protocol is usable.</summary>
    Warning,

    /// <summary>Modern protocol is disabled.</summary>
    Attention,

    /// <summary>Not supported on this OS.</summary>
    Neutral,
}

public sealed record SettingOption(ConfiguredSetting Value, string Label)
{
    /// <summary>Used as the combo box item's accessible name.</summary>
    public override string ToString() => Label;
}

/// <summary>One protocol role. Changes are only staged here; in read-only mode they serve as a preview.</summary>
public sealed class ProtocolCellViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private ConfiguredSetting _selected;

    public ProtocolCellViewModel(ProtocolRoleState state, Action onChanged)
    {
        State = state;
        _onChanged = onChanged;
        _selected = state.Configured;

        var options = new List<SettingOption>
        {
            new(ConfiguredSetting.NotConfigured, $"OS default ({ProtocolStateEvaluator.Describe(state.OsDefault)})"),
            new(ConfiguredSetting.Enabled, "Enabled"),
            new(ConfiguredSetting.Disabled, "Disabled"),
        };
        if (state.Configured == ConfiguredSetting.Custom)
        {
            options.Add(new SettingOption(ConfiguredSetting.Custom, "Custom (keep current values)"));
        }

        Options = options;
    }

    public ProtocolRoleState State { get; }

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

    public string RoleLabel => State.Role == ProtocolRole.Server ? "Server" : "Client";

    /// <summary>Accessible name of the setting combo box, e.g. "TLS 1.2 server setting".</summary>
    public string AccessibleName => $"{State.Protocol.DisplayName} {RoleLabel.ToLowerInvariant()} setting";

    public string EffectiveText => ProtocolStateEvaluator.Describe(State.Effective);

    public string SourceText => State.IsExplicitlyConfigured ? "Set in registry" : "OS default (not configured)";

    /// <summary>One-line source label for the compact and list layouts.</summary>
    public string ShortSourceText => State.IsExplicitlyConfigured ? "· set in registry" : "· OS default";

    public string PendingText =>
        IsDirty ? $"Pending: {ProtocolStateEvaluator.Describe(State.With(Selected).Effective)} after restart" : string.Empty;

    /// <summary>One-line pending label for the compact layout.</summary>
    public string ShortPendingText =>
        IsDirty ? $"→ {ProtocolStateEvaluator.Describe(State.With(Selected).Effective)}" : string.Empty;

    public StatusTone Tone => State.Effective switch
    {
        EffectiveState.NotSupported => StatusTone.Neutral,
        _ when State.Protocol.IsDeprecated && ProtocolStateEvaluator.IsUsable(State.Effective) => StatusTone.Warning,
        EffectiveState.Disabled when !State.Protocol.IsDeprecated => StatusTone.Attention,
        _ => StatusTone.Good,
    };

    public string ToolTip =>
        $"HKLM\\{State.KeyPath}\n" +
        $"Enabled = {Format(State.Values.Enabled)}\n" +
        $"DisabledByDefault = {Format(State.Values.DisabledByDefault)}\n" +
        $"OS default on this build: {ProtocolStateEvaluator.Describe(State.OsDefault)}";

    public PendingChange? ToPendingChange() =>
        IsDirty && Selected != ConfiguredSetting.Custom ? new PendingChange(State.Protocol, State.Role, Selected) : null;

    private static string Format(uint? value) => value is { } v ? $"0x{v:X8} ({v})" : "(not set)";
}

public sealed class ProtocolRowViewModel : ObservableObject
{
    public ProtocolRowViewModel(SchannelProtocol protocol, ProtocolCellViewModel server, ProtocolCellViewModel client)
    {
        Protocol = protocol;
        Server = server;
        Client = client;
        ResetToOsDefaultCommand = new RelayCommand(() =>
        {
            Server.Selected = ConfiguredSetting.NotConfigured;
            Client.Selected = ConfiguredSetting.NotConfigured;
        });

        Server.PropertyChanged += OnCellPropertyChanged;
        Client.PropertyChanged += OnCellPropertyChanged;
    }

    public SchannelProtocol Protocol { get; }

    public string Name => Protocol.DisplayName;

    /// <summary>Used as the grid row's accessible name.</summary>
    public override string ToString() => Name;

    public string Description => Protocol.Description;

    public bool IsDeprecated => Protocol.IsDeprecated;

    public ProtocolCellViewModel Server { get; }

    public ProtocolCellViewModel Client { get; }

    /// <summary>True when either side has a staged change (used for live filtering).</summary>
    public bool HasPendingChanges => Server.IsDirty || Client.IsDirty;

    public ICommand ResetToOsDefaultCommand { get; }

    public ProtocolCellViewModel Cell(ProtocolRole role) => role == ProtocolRole.Server ? Server : Client;

    public ProtocolRowView ToView() => new(Protocol, Server.State, Client.State, Server.IsDirty, Client.IsDirty);

    private void OnCellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProtocolCellViewModel.IsDirty))
        {
            OnPropertyChanged(nameof(HasPendingChanges));
        }
    }
}
