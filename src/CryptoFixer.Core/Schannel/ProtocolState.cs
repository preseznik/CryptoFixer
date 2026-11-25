namespace CryptoFixer.Core.Schannel;

/// <summary>What is configured in the registry for one protocol role.</summary>
public enum ConfiguredSetting
{
    /// <summary>Neither Enabled nor DisabledByDefault is set; Windows uses its built-in default.</summary>
    NotConfigured,

    /// <summary>Enabled is non-zero and DisabledByDefault is 0 or missing.</summary>
    Enabled,

    /// <summary>Enabled is 0.</summary>
    Disabled,

    /// <summary>Any other combination (e.g. Enabled=1 with DisabledByDefault=1, or only DisabledByDefault set).</summary>
    Custom,
}

/// <summary>The state Schannel actually uses after combining registry values with the OS default.</summary>
public enum EffectiveState
{
    Enabled,

    /// <summary>Enabled, but DisabledByDefault=1: only used by applications that explicitly request it.</summary>
    EnabledOnRequestOnly,

    Disabled,

    NotSupported,
}

public static class SchannelPaths
{
    public const string Schannel = @"SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL";
    public const string Protocols = Schannel + @"\Protocols";
    public const string EnabledValue = "Enabled";
    public const string DisabledByDefaultValue = "DisabledByDefault";

    public static string ProtocolKey(SchannelProtocol protocol) => $"{Protocols}\\{protocol.KeyName}";

    public static string RoleKey(SchannelProtocol protocol, ProtocolRole role) => $"{ProtocolKey(protocol)}\\{role}";

    /// <summary>SCHANNEL\Ciphers, SCHANNEL\Hashes or SCHANNEL\KeyExchangeAlgorithms.</summary>
    public static string AlgorithmFolder(AlgorithmCategory category) => $"{Schannel}\\{AlgorithmCatalog.FolderName(category)}";

    /// <summary>The algorithm key. Its last segment may contain '/', e.g. "RC4 128/128", which is part of the key name.</summary>
    public static string AlgorithmKey(SchannelAlgorithm algorithm) => $"{AlgorithmFolder(algorithm.Category)}\\{algorithm.KeyName}";
}

/// <summary>Raw Enabled / DisabledByDefault values of a protocol role key (null = value not present).</summary>
public sealed record ProtocolRegistryValues(uint? Enabled, uint? DisabledByDefault)
{
    public static ProtocolRegistryValues None { get; } = new(null, null);

    public bool IsEmpty => Enabled is null && DisabledByDefault is null;

    /// <summary>Values written by this tool for a setting. Returns null for <see cref="ConfiguredSetting.Custom"/>.</summary>
    public static ProtocolRegistryValues? For(ConfiguredSetting setting) => setting switch
    {
        ConfiguredSetting.NotConfigured => None,
        ConfiguredSetting.Enabled => new ProtocolRegistryValues(1, 0),
        ConfiguredSetting.Disabled => new ProtocolRegistryValues(0, 1),
        _ => null,
    };
}

public sealed record ProtocolRoleState(
    SchannelProtocol Protocol,
    ProtocolRole Role,
    ProtocolRegistryValues Values,
    OsDefaultState OsDefault)
{
    public ConfiguredSetting Configured => ProtocolStateEvaluator.Classify(Values);

    public EffectiveState Effective => ProtocolStateEvaluator.Evaluate(Values, OsDefault);

    /// <summary>True when the effective state comes from explicit registry values rather than the OS default.</summary>
    public bool IsExplicitlyConfigured => !Values.IsEmpty;

    public string KeyPath => SchannelPaths.RoleKey(Protocol, Role);

    /// <summary>Returns the state as it would be after applying <paramref name="target"/>.</summary>
    public ProtocolRoleState With(ConfiguredSetting target) =>
        ProtocolRegistryValues.For(target) is { } values ? this with { Values = values } : this;
}

/// <summary>A staged change: set <see cref="Target"/> for one protocol role.</summary>
public sealed record PendingChange(SchannelProtocol Protocol, ProtocolRole Role, ConfiguredSetting Target)
{
    public override string ToString() => $"{Protocol.KeyName} {Role}: {Describe(Target)}";

    public static string Describe(ConfiguredSetting setting) => setting switch
    {
        ConfiguredSetting.NotConfigured => "reset to OS default",
        ConfiguredSetting.Enabled => "enable",
        ConfiguredSetting.Disabled => "disable",
        _ => "keep custom values",
    };
}

public static class ProtocolStateEvaluator
{
    public static ConfiguredSetting Classify(ProtocolRegistryValues values) => values switch
    {
        { Enabled: null, DisabledByDefault: null } => ConfiguredSetting.NotConfigured,
        { Enabled: 0 } => ConfiguredSetting.Disabled,
        { Enabled: not null, DisabledByDefault: null or 0 } => ConfiguredSetting.Enabled,
        _ => ConfiguredSetting.Custom,
    };

    public static EffectiveState Evaluate(ProtocolRegistryValues values, OsDefaultState osDefault)
    {
        if (osDefault == OsDefaultState.NotSupported)
        {
            return EffectiveState.NotSupported;
        }

        var enabled = values.Enabled is { } e ? e != 0 : osDefault == OsDefaultState.Enabled;
        if (!enabled)
        {
            return EffectiveState.Disabled;
        }

        return values.DisabledByDefault is { } d && d != 0
            ? EffectiveState.EnabledOnRequestOnly
            : EffectiveState.Enabled;
    }

    public static bool IsUsable(EffectiveState state) =>
        state is EffectiveState.Enabled or EffectiveState.EnabledOnRequestOnly;

    public static string Describe(EffectiveState state) => state switch
    {
        EffectiveState.Enabled => "Enabled",
        EffectiveState.EnabledOnRequestOnly => "Enabled (on request only)",
        EffectiveState.Disabled => "Disabled",
        EffectiveState.NotSupported => "Not supported",
        _ => state.ToString(),
    };

    public static string Describe(OsDefaultState state) => state switch
    {
        OsDefaultState.Enabled => "Enabled",
        OsDefaultState.Disabled => "Disabled",
        OsDefaultState.NotSupported => "Not supported",
        _ => state.ToString(),
    };
}
