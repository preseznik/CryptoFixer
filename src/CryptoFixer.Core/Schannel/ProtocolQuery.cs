namespace CryptoFixer.Core.Schannel;

/// <summary>State-based quick filters. They use the current effective state; staged changes only count for <see cref="PendingChanges"/>.</summary>
public enum ProtocolStateFilter
{
    All,
    Enabled,
    Disabled,
    NotSupported,
    SetInRegistry,
    OsDefault,
    PendingChanges,
    Deprecated,
}

/// <summary>Which side(s) of a protocol the state filter looks at (and the UI shows).</summary>
public enum RoleScope
{
    Both,
    Server,
    Client,
}

public enum ProtocolSortKey
{
    /// <summary>Catalog order: oldest protocol first.</summary>
    Version,
    Name,
    ServerState,
    ClientState,
    ServerSource,
    ClientSource,
}

/// <summary>Snapshot of one protocol row (both roles) used for filtering and sorting.</summary>
public sealed record ProtocolRowView(
    SchannelProtocol Protocol,
    ProtocolRoleState Server,
    ProtocolRoleState Client,
    bool ServerPending = false,
    bool ClientPending = false)
{
    public ProtocolRoleState Get(ProtocolRole role) => role == ProtocolRole.Server ? Server : Client;

    public bool IsPending(ProtocolRole role) => role == ProtocolRole.Server ? ServerPending : ClientPending;
}

public enum AlgorithmSortKey
{
    /// <summary>Catalog order: weakest first.</summary>
    Strength,
    Name,
    State,
    Source,
}

/// <summary>Snapshot of one cipher, hash or key exchange row used for filtering and sorting.</summary>
public sealed record AlgorithmRowView(AlgorithmState State, bool Pending = false)
{
    public SchannelAlgorithm Algorithm => State.Algorithm;
}

/// <summary>Sorts algorithm rows; ties are broken by catalog order (weakest first).</summary>
public sealed class AlgorithmRowComparer(AlgorithmSortKey key, bool descending = false) : IComparer<AlgorithmRowView>
{
    public AlgorithmSortKey Key { get; } = key;

    public bool Descending { get; } = descending;

    public int Compare(AlgorithmRowView? x, AlgorithmRowView? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null || y is null)
        {
            return x is null ? -1 : 1;
        }

        var primary = Key switch
        {
            AlgorithmSortKey.Strength => Index(x).CompareTo(Index(y)),
            AlgorithmSortKey.Name => string.Compare(x.Algorithm.KeyName, y.Algorithm.KeyName, StringComparison.OrdinalIgnoreCase),
            AlgorithmSortKey.State => Rank(x.State.Effective).CompareTo(Rank(y.State.Effective)),
            AlgorithmSortKey.Source => (x.State.IsExplicitlyConfigured ? 0 : 1).CompareTo(y.State.IsExplicitlyConfigured ? 0 : 1),
            _ => 0,
        };

        if (Descending)
        {
            primary = -primary;
        }

        return primary != 0 ? primary : Index(x).CompareTo(Index(y));
    }

    private static int Index(AlgorithmRowView row) => AlgorithmCatalog.IndexOf(row.Algorithm) is var i and >= 0 ? i : int.MaxValue;

    private static int Rank(EffectiveState state) => state switch
    {
        EffectiveState.Enabled => 0,
        EffectiveState.EnabledOnRequestOnly => 1,
        EffectiveState.Disabled => 2,
        _ => 3,
    };
}

public sealed record ProtocolFilter(string? SearchText = null, ProtocolStateFilter State = ProtocolStateFilter.All, RoleScope Roles = RoleScope.Both)
{
    public bool IsActive => !string.IsNullOrWhiteSpace(SearchText) || State != ProtocolStateFilter.All || Roles != RoleScope.Both;

    public IReadOnlyList<ProtocolRole> RolesInScope => Roles switch
    {
        RoleScope.Server => [ProtocolRole.Server],
        RoleScope.Client => [ProtocolRole.Client],
        _ => [ProtocolRole.Server, ProtocolRole.Client],
    };

    public bool Matches(ProtocolRowView row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return MatchesText(row.Protocol.KeyName, row.Protocol.Description, row.Protocol.IsDeprecated) && MatchesState(row);
    }

    /// <summary>Filters a cipher, hash or key exchange row. <see cref="Roles"/> doesn't apply: algorithms have no sides.</summary>
    public bool Matches(AlgorithmRowView row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var algorithm = row.Algorithm;
        if (!MatchesText(algorithm.KeyName, algorithm.Description, algorithm.IsDeprecated))
        {
            return false;
        }

        var state = row.State;
        return State switch
        {
            ProtocolStateFilter.All => true,
            ProtocolStateFilter.Deprecated => algorithm.IsDeprecated,
            ProtocolStateFilter.Enabled => state.Effective == EffectiveState.Enabled,
            ProtocolStateFilter.Disabled => state.Effective == EffectiveState.Disabled,
            ProtocolStateFilter.NotSupported => state.Effective == EffectiveState.NotSupported,
            ProtocolStateFilter.SetInRegistry => state.IsExplicitlyConfigured,
            ProtocolStateFilter.OsDefault => !state.IsExplicitlyConfigured,
            ProtocolStateFilter.PendingChanges => row.Pending,
            _ => true,
        };
    }

    /// <summary>Every whitespace-separated term must occur in the name or description ("tls1.2" also matches "TLS 1.2").</summary>
    private bool MatchesText(string name, string description, bool isDeprecated)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var haystack = $"{name} {description}{(isDeprecated ? " deprecated" : string.Empty)}";
        var compact = haystack.Replace(" ", string.Empty, StringComparison.Ordinal);
        return SearchText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase) || compact.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private bool MatchesState(ProtocolRowView row) => State switch
    {
        ProtocolStateFilter.All => true,
        ProtocolStateFilter.Deprecated => row.Protocol.IsDeprecated,
        _ => RolesInScope.Any(role => MatchesRole(row, role)),
    };

    private bool MatchesRole(ProtocolRowView row, ProtocolRole role)
    {
        var state = row.Get(role);
        return State switch
        {
            ProtocolStateFilter.Enabled => ProtocolStateEvaluator.IsUsable(state.Effective),
            ProtocolStateFilter.Disabled => state.Effective == EffectiveState.Disabled,
            ProtocolStateFilter.NotSupported => state.Effective == EffectiveState.NotSupported,
            ProtocolStateFilter.SetInRegistry => state.IsExplicitlyConfigured,
            ProtocolStateFilter.OsDefault => !state.IsExplicitlyConfigured,
            ProtocolStateFilter.PendingChanges => row.IsPending(role),
            _ => true,
        };
    }
}

/// <summary>Sorts rows by <see cref="ProtocolSortKey"/>; ties are always broken by protocol version (oldest first).</summary>
public sealed class ProtocolRowComparer : IComparer<ProtocolRowView>
{
    private static readonly Dictionary<SchannelProtocol, int> VersionOrder =
        ProtocolCatalog.All.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);

    public ProtocolRowComparer(ProtocolSortKey key, bool descending = false)
    {
        Key = key;
        Descending = descending;
    }

    public ProtocolSortKey Key { get; }

    public bool Descending { get; }

    public int Compare(ProtocolRowView? x, ProtocolRowView? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null || y is null)
        {
            return x is null ? -1 : 1;
        }

        var primary = Key switch
        {
            ProtocolSortKey.Version => VersionIndex(x).CompareTo(VersionIndex(y)),
            ProtocolSortKey.Name => string.Compare(x.Protocol.KeyName, y.Protocol.KeyName, StringComparison.OrdinalIgnoreCase),
            ProtocolSortKey.ServerState => StateRank(x.Server).CompareTo(StateRank(y.Server)),
            ProtocolSortKey.ClientState => StateRank(x.Client).CompareTo(StateRank(y.Client)),
            ProtocolSortKey.ServerSource => SourceRank(x.Server).CompareTo(SourceRank(y.Server)),
            ProtocolSortKey.ClientSource => SourceRank(x.Client).CompareTo(SourceRank(y.Client)),
            _ => 0,
        };

        if (Descending)
        {
            primary = -primary;
        }

        return primary != 0 ? primary : VersionIndex(x).CompareTo(VersionIndex(y));
    }

    private static int VersionIndex(ProtocolRowView row) =>
        VersionOrder.TryGetValue(row.Protocol, out var index) ? index : int.MaxValue;

    /// <summary>Enabled first, then on-request only, disabled, not supported.</summary>
    private static int StateRank(ProtocolRoleState state) => state.Effective switch
    {
        EffectiveState.Enabled => 0,
        EffectiveState.EnabledOnRequestOnly => 1,
        EffectiveState.Disabled => 2,
        _ => 3,
    };

    /// <summary>Explicit registry settings first, then OS defaults.</summary>
    private static int SourceRank(ProtocolRoleState state) => state.IsExplicitlyConfigured ? 0 : 1;
}
