using CryptoFixer.Core.Schannel;

namespace CryptoFixer.Core.Presets;

/// <summary>One protocol role setting inside a preset. <see cref="ConfiguredSetting.Custom"/> is never stored.</summary>
public sealed record PresetEntry(SchannelProtocol Protocol, ProtocolRole Role, ConfiguredSetting Setting);

/// <summary>
/// A named set of protocol settings. Protocol roles that are not listed are left unchanged when the preset is staged.
/// </summary>
public sealed record ProtocolPreset(string Name, IReadOnlyList<PresetEntry> Entries)
{
    public string? Description { get; init; }

    public bool IsBuiltIn { get; init; }

    public DateTimeOffset? SavedAt { get; init; }

    /// <summary>Free text describing where the preset was captured, e.g. machine name and Windows version.</summary>
    public string? SavedFrom { get; init; }

    /// <summary>
    /// Captures the given settings (typically what the UI currently shows, including staged changes) as a preset.
    /// Entries with <see cref="ConfiguredSetting.Custom"/> cannot be represented and are returned in <paramref name="skipped"/>.
    /// </summary>
    public static ProtocolPreset Capture(
        string name,
        string? description,
        IEnumerable<PresetEntry> settings,
        DateTimeOffset savedAt,
        string? savedFrom,
        out IReadOnlyList<PresetEntry> skipped)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(settings);

        var all = settings.ToList();
        skipped = all.Where(e => e.Setting == ConfiguredSetting.Custom).ToList();
        return new ProtocolPreset(name.Trim(), Normalize(all.Where(e => e.Setting != ConfiguredSetting.Custom)))
        {
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            SavedAt = savedAt,
            SavedFrom = savedFrom,
        };
    }

    /// <summary>Orders entries by protocol version and role; a later duplicate wins.</summary>
    internal static IReadOnlyList<PresetEntry> Normalize(IEnumerable<PresetEntry> entries)
    {
        var order = ProtocolCatalog.All.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        return entries
            .GroupBy(e => (e.Protocol, e.Role))
            .Select(g => g.Last())
            .OrderBy(e => order.TryGetValue(e.Protocol, out var i) ? i : int.MaxValue)
            .ThenBy(e => e.Role)
            .ToList();
    }
}

/// <param name="Targets">Settings to stage (including ones that already match the current state).</param>
/// <param name="Skipped">Human-readable notes about preset entries that were not staged.</param>
public sealed record PresetPlan(IReadOnlyList<PendingChange> Targets, IReadOnlyList<string> Skipped)
{
    /// <summary>Targets that differ from what is configured in the registry.</summary>
    public IReadOnlyList<PendingChange> DifferencesFrom(IReadOnlyList<ProtocolRoleState> current) =>
        Targets.Where(t => current.Any(s => s.Protocol == t.Protocol && s.Role == t.Role && s.Configured != t.Target)).ToList();
}

public static class PresetPlanner
{
    /// <summary>
    /// Works out what staging <paramref name="preset"/> means on this machine. Enabling a protocol the OS doesn't support is skipped,
    /// so a preset captured on a newer Windows version (e.g. with TLS 1.3) can be used on older ones.
    /// </summary>
    public static PresetPlan Plan(ProtocolPreset preset, IReadOnlyList<ProtocolRoleState> current)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(current);

        var targets = new List<PendingChange>();
        var skipped = new List<string>();
        foreach (var entry in preset.Entries)
        {
            var state = current.FirstOrDefault(s => s.Protocol == entry.Protocol && s.Role == entry.Role);
            if (state is null || entry.Setting == ConfiguredSetting.Custom)
            {
                continue;
            }

            if (entry.Setting == ConfiguredSetting.Enabled && state.OsDefault == OsDefaultState.NotSupported)
            {
                skipped.Add($"{entry.Protocol.KeyName} {entry.Role}: not supported on this Windows build, left unchanged");
                continue;
            }

            targets.Add(new PendingChange(entry.Protocol, entry.Role, entry.Setting));
        }

        return new PresetPlan(targets, skipped);
    }
}

/// <summary>Presets that ship with the app. They adapt to the Windows build of the machine they are created for.</summary>
public static class BuiltInPresets
{
    public const string ModernName = "Modern: TLS 1.2 and 1.3 only";
    public const string OsDefaultsName = "OS defaults: remove all settings";

    public static IReadOnlyList<string> Names { get; } = [ModernName, OsDefaultsName];

    public static bool IsReservedName(string name) =>
        Names.Any(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <param name="states">Current states; only their OS defaults (i.e. the Windows build) are used.</param>
    public static IReadOnlyList<ProtocolPreset> For(IReadOnlyList<ProtocolRoleState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        var modern = states
            .Select(s => (State: s, Target: ModernTarget(s, states)))
            .Where(x => x.Target is not null)
            .Select(x => new PresetEntry(x.State.Protocol, x.State.Role, x.Target!.Value));

        var osDefaults = states.Select(s => new PresetEntry(s.Protocol, s.Role, ConfiguredSetting.NotConfigured));

        return
        [
            new ProtocolPreset(ModernName, ProtocolPreset.Normalize(modern))
            {
                IsBuiltIn = true,
                Description = "Disables PCT, Unified Hello, SSL 2.0/3.0, TLS 1.0/1.1 and DTLS 1.0 (where DTLS 1.2 exists). " +
                              "Enables TLS 1.2, DTLS 1.2 and TLS 1.3 where this Windows build supports them.",
            },
            new ProtocolPreset(OsDefaultsName, ProtocolPreset.Normalize(osDefaults))
            {
                IsBuiltIn = true,
                Description = "Removes every Enabled/DisabledByDefault value so Windows uses its built-in defaults.",
            },
        ];
    }

    private static ConfiguredSetting? ModernTarget(ProtocolRoleState state, IReadOnlyList<ProtocolRoleState> all)
    {
        var protocol = state.Protocol;

        if (protocol == ProtocolCatalog.Dtls10)
        {
            var dtls12Supported = all.Any(s => s.Protocol == ProtocolCatalog.Dtls12 && s.Role == state.Role && s.OsDefault != OsDefaultState.NotSupported);
            return dtls12Supported ? ConfiguredSetting.Disabled : null;
        }

        if (protocol.IsDeprecated)
        {
            return ConfiguredSetting.Disabled;
        }

        return state.OsDefault == OsDefaultState.NotSupported ? ConfiguredSetting.NotConfigured : ConfiguredSetting.Enabled;
    }
}
