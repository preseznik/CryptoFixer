using CryptoFixer.Core.Schannel;

namespace CryptoFixer.Tests;

public class ProtocolQueryTests
{
    private const int Server2022 = 20348;

    private static IReadOnlyList<ProtocolRowView> Rows(InMemoryRegistry registry, Func<SchannelProtocol, ProtocolRole, bool>? pending = null)
    {
        var states = new SchannelProtocolStore(registry).ReadAll(Server2022);
        return ProtocolCatalog.All
            .Select(p => new ProtocolRowView(
                p,
                states.Single(s => s.Protocol == p && s.Role == ProtocolRole.Server),
                states.Single(s => s.Protocol == p && s.Role == ProtocolRole.Client),
                pending?.Invoke(p, ProtocolRole.Server) ?? false,
                pending?.Invoke(p, ProtocolRole.Client) ?? false))
            .ToList();
    }

    private static List<string> Names(IEnumerable<ProtocolRowView> rows, ProtocolFilter filter) =>
        rows.Where(filter.Matches).Select(r => r.Protocol.KeyName).ToList();

    [Fact]
    public void Search_matches_all_terms_in_name_or_description_ignoring_spaces()
    {
        var rows = Rows(new InMemoryRegistry());

        Assert.Equal(["TLS 1.2", "DTLS 1.2"], Names(rows, new ProtocolFilter("tls1.2")));
        Assert.Equal(["DTLS 1.0"], Names(rows, new ProtocolFilter("  DTLS   deprecated ")));
        Assert.Empty(Names(rows, new ProtocolFilter("quic")));
        Assert.Equal(10, Names(rows, new ProtocolFilter("   ")).Count);
    }

    [Fact]
    public void State_filters_respect_the_selected_side()
    {
        var registry = new InMemoryRegistry();
        registry.SetProtocol(ProtocolCatalog.Tls10, ProtocolRole.Server, 0, 1);
        var rows = Rows(registry);

        Assert.Contains("TLS 1.0", Names(rows, new ProtocolFilter(State: ProtocolStateFilter.Disabled, Roles: RoleScope.Server)));
        Assert.DoesNotContain("TLS 1.0", Names(rows, new ProtocolFilter(State: ProtocolStateFilter.Disabled, Roles: RoleScope.Client)));
        Assert.Equal(["TLS 1.0"], Names(rows, new ProtocolFilter(State: ProtocolStateFilter.SetInRegistry)));
        Assert.DoesNotContain("TLS 1.0", Names(rows, new ProtocolFilter(State: ProtocolStateFilter.OsDefault, Roles: RoleScope.Server)));
        Assert.Equal(["PCT 1.0", "SSL 2.0"], Names(rows, new ProtocolFilter(State: ProtocolStateFilter.NotSupported)));

        var enabled = Names(rows, new ProtocolFilter(State: ProtocolStateFilter.Enabled));
        Assert.Contains("TLS 1.3", enabled);
        Assert.DoesNotContain("SSL 3.0", enabled);
    }

    [Fact]
    public void Pending_and_deprecated_filters()
    {
        var rows = Rows(new InMemoryRegistry(), (p, r) => p == ProtocolCatalog.Tls11 && r == ProtocolRole.Client);

        Assert.Equal(["TLS 1.1"], Names(rows, new ProtocolFilter(State: ProtocolStateFilter.PendingChanges)));
        Assert.Empty(Names(rows, new ProtocolFilter(State: ProtocolStateFilter.PendingChanges, Roles: RoleScope.Server)));
        Assert.Equal(
            ProtocolCatalog.All.Where(p => p.IsDeprecated).Select(p => p.KeyName),
            Names(rows, new ProtocolFilter(State: ProtocolStateFilter.Deprecated, Roles: RoleScope.Client)));
        Assert.Equal(["TLS 1.1"], Names(rows, new ProtocolFilter("tls", ProtocolStateFilter.PendingChanges)));
    }

    [Fact]
    public void Sorting_by_state_orders_enabled_first_and_breaks_ties_by_version()
    {
        var registry = new InMemoryRegistry();
        registry.SetProtocol(ProtocolCatalog.Tls10, ProtocolRole.Server, 0, 1);
        var rows = Rows(registry);

        var ascending = rows.OrderBy(r => r, new ProtocolRowComparer(ProtocolSortKey.ServerState)).Select(r => r.Protocol.KeyName).ToList();
        var descending = rows.OrderBy(r => r, new ProtocolRowComparer(ProtocolSortKey.ServerState, descending: true)).Select(r => r.Protocol.KeyName).ToList();

        // Server 2022 defaults: Unified Hello, TLS 1.1-1.3, DTLS enabled; SSL 3.0 and (configured) TLS 1.0 disabled; PCT, SSL 2.0 unsupported.
        Assert.Equal(
            ["Multi-Protocol Unified Hello", "TLS 1.1", "TLS 1.2", "TLS 1.3", "DTLS 1.0", "DTLS 1.2", "SSL 3.0", "TLS 1.0", "PCT 1.0", "SSL 2.0"],
            ascending);
        Assert.Equal(
            ["PCT 1.0", "SSL 2.0", "SSL 3.0", "TLS 1.0", "Multi-Protocol Unified Hello", "TLS 1.1", "TLS 1.2", "TLS 1.3", "DTLS 1.0", "DTLS 1.2"],
            descending);
    }

    [Fact]
    public void Sorting_by_name_source_and_version()
    {
        var registry = new InMemoryRegistry();
        registry.SetProtocol(ProtocolCatalog.Tls13, ProtocolRole.Client, 1, 0);
        registry.SetProtocol(ProtocolCatalog.Ssl30, ProtocolRole.Client, 0, 1);
        var rows = Rows(registry).Reverse().ToList();

        static List<string> Sorted(IEnumerable<ProtocolRowView> rows, ProtocolSortKey key, bool descending = false) =>
            rows.OrderBy(r => r, new ProtocolRowComparer(key, descending)).Select(r => r.Protocol.KeyName).ToList();

        Assert.Equal(ProtocolCatalog.All.Select(p => p.KeyName), Sorted(rows, ProtocolSortKey.Version));
        Assert.Equal("DTLS 1.0", Sorted(rows, ProtocolSortKey.Name)[0]);
        Assert.Equal("TLS 1.3", Sorted(rows, ProtocolSortKey.Name, descending: true)[0]);
        Assert.Equal(["SSL 3.0", "TLS 1.3"], Sorted(rows, ProtocolSortKey.ClientSource).Take(2));
        Assert.Equal(["SSL 3.0", "TLS 1.3"], Sorted(rows, ProtocolSortKey.ClientSource, descending: true).TakeLast(2));
    }
}
