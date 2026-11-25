using CryptoFixer.Core.Registry;

namespace CryptoFixer.Core.Schannel;

/// <summary>
/// Reads and writes Schannel settings (protocols, ciphers, hashes and key exchange algorithms) through an
/// <see cref="IRegistryAccess"/>, and reads the active cipher suite list.
/// </summary>
public sealed class SchannelProtocolStore
{
    private readonly IRegistryAccess _registry;

    public SchannelProtocolStore(IRegistryAccess registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public IRegistryAccess Registry => _registry;

    public ProtocolRegistryValues ReadValues(SchannelProtocol protocol, ProtocolRole role)
    {
        var key = SchannelPaths.RoleKey(protocol, role);
        return new ProtocolRegistryValues(
            _registry.GetValue(key, SchannelPaths.EnabledValue)?.AsUInt32(),
            _registry.GetValue(key, SchannelPaths.DisabledByDefaultValue)?.AsUInt32());
    }

    public ProtocolRoleState Read(SchannelProtocol protocol, ProtocolRole role, int osBuild) =>
        new(protocol, role, ReadValues(protocol, role), OsDefaults.Get(protocol, role, osBuild));

    public IReadOnlyList<ProtocolRoleState> ReadAll(int osBuild) =>
        ProtocolCatalog.All
            .SelectMany(p => new[] { Read(p, ProtocolRole.Server, osBuild), Read(p, ProtocolRole.Client, osBuild) })
            .ToList();

    /// <summary>Writes the given changes. Enabled/Disabled write both Enabled and DisabledByDefault;
    /// NotConfigured deletes both values so Windows falls back to its default.</summary>
    public void Apply(IEnumerable<PendingChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var list = changes.ToList();

        foreach (var change in list)
        {
            if (change.Target == ConfiguredSetting.Custom)
            {
                throw new ArgumentException($"Cannot apply a custom setting ({change.Protocol} {change.Role}).", nameof(changes));
            }
        }

        foreach (var change in list)
        {
            var key = SchannelPaths.RoleKey(change.Protocol, change.Role);
            var values = ProtocolRegistryValues.For(change.Target)!;

            if (values.IsEmpty)
            {
                _registry.DeleteValue(key, SchannelPaths.EnabledValue);
                _registry.DeleteValue(key, SchannelPaths.DisabledByDefaultValue);
            }
            else
            {
                _registry.SetDWord(key, SchannelPaths.EnabledValue, values.Enabled!.Value);
                _registry.SetDWord(key, SchannelPaths.DisabledByDefaultValue, values.DisabledByDefault!.Value);
            }
        }
    }

    public AlgorithmState Read(SchannelAlgorithm algorithm, int osBuild) => Read(algorithm, osBuild, keyExists: true);

    /// <summary>Reads every cipher, hash and key exchange algorithm. Each SCHANNEL folder is listed once so that
    /// keys that don't exist cost no further registry calls (this matters for remote computers).</summary>
    public IReadOnlyList<AlgorithmState> ReadAllAlgorithms(int osBuild)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in Enum.GetValues<AlgorithmCategory>())
        {
            var folder = SchannelPaths.AlgorithmFolder(category);
            existing.UnionWith(_registry.GetSubKeyNames(folder).Select(name => $"{folder}\\{name}"));
        }

        return AlgorithmCatalog.All
            .Select(a => Read(a, osBuild, existing.Contains(SchannelPaths.AlgorithmKey(a))))
            .ToList();
    }

    /// <summary>Reads protocols, algorithms and the active cipher suite list in one go.</summary>
    public SchannelSnapshot ReadSnapshot(int osBuild) =>
        new(ReadAll(osBuild), ReadAllAlgorithms(osBuild), ActiveCipherSuites.Read(_registry));

    /// <summary>Writes algorithm changes: Enabled writes 0xffffffff, Disabled writes 0 and NotConfigured deletes the
    /// Enabled value. Other values in the key (e.g. key sizes) are left alone.</summary>
    public void Apply(IEnumerable<AlgorithmChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var list = changes.ToList();

        foreach (var change in list)
        {
            if (change.Target == ConfiguredSetting.Custom)
            {
                throw new ArgumentException($"Cannot apply a custom setting ({change.Algorithm}).", nameof(changes));
            }
        }

        foreach (var change in list)
        {
            var key = SchannelPaths.AlgorithmKey(change.Algorithm);
            switch (change.Target)
            {
                case ConfiguredSetting.NotConfigured:
                    _registry.DeleteValue(key, SchannelPaths.EnabledValue);
                    break;
                case ConfiguredSetting.Enabled:
                    _registry.SetDWord(key, SchannelPaths.EnabledValue, AlgorithmState.EnabledValue);
                    break;
                default:
                    _registry.SetDWord(key, SchannelPaths.EnabledValue, 0);
                    break;
            }
        }
    }

    private AlgorithmState Read(SchannelAlgorithm algorithm, int osBuild, bool keyExists)
    {
        var key = SchannelPaths.AlgorithmKey(algorithm);
        uint? Value(string name) => keyExists ? _registry.GetValue(key, name)?.AsUInt32() : null;

        var sizes = AlgorithmDefaults.KeySizeValues(algorithm, osBuild)
            .Select(v => new KeySizeSetting(v.ValueName, Value(v.ValueName), v.DefaultText))
            .ToList();

        return new AlgorithmState(algorithm, Value(SchannelPaths.EnabledValue), AlgorithmDefaults.Get(algorithm, osBuild))
        {
            KeySizes = sizes,
        };
    }
}

/// <summary>Everything the main view shows for one computer.</summary>
public sealed record SchannelSnapshot(
    IReadOnlyList<ProtocolRoleState> Protocols,
    IReadOnlyList<AlgorithmState> Algorithms,
    ActiveCipherSuites CipherSuites);
