using CryptoFixer.Core.Registry;

namespace CryptoFixer.Core.Schannel;

/// <summary>Reads and writes Schannel protocol settings through an <see cref="IRegistryAccess"/>.</summary>
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
}
