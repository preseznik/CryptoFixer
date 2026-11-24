using System.Security;
using System.Security.Cryptography;
using CryptoFixer.Core.Remote;

namespace CryptoFixer.Tests;

public class MachineListFileTests
{
    private static readonly byte[] Blob = [1, 2, 3, 250];

    [Fact]
    public void Serialize_then_parse_round_trips_every_field()
    {
        MachineEntry[] machines =
        [
            new("web01", "10.0.0.5", RemoteProtocol.WsMan, MachineCredential.CurrentAccount),
            new("db01", "db01.contoso.com", RemoteProtocol.WsManHttps, MachineCredential.ForUser(@"CONTOSO\admin", Blob)),
            new("legacy", "fe80::1", RemoteProtocol.Dcom, MachineCredential.ForUser("admin@contoso.com")),
        ];

        var json = MachineListFile.Serialize(machines);
        var result = MachineListFile.Parse(json);

        Assert.Null(result.Error);
        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Machines.Count);
        Assert.Equal(machines[0], result.Machines[0]);
        Assert.Equal(("db01", "db01.contoso.com", RemoteProtocol.WsManHttps, @"CONTOSO\admin"),
            (result.Machines[1].Name, result.Machines[1].Host, result.Machines[1].Protocol, result.Machines[1].Credential.UserName));
        Assert.Equal(Blob, result.Machines[1].Credential.ProtectedPassword);
        Assert.False(result.Machines[2].Credential.HasSavedPassword);
        Assert.Equal(RemoteProtocol.Dcom, result.Machines[2].Protocol);
        Assert.Contains("\"protectedPassword\": \"AQID+g==\"", json, StringComparison.Ordinal);
        Assert.Contains("\"credential\": \"current\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Lenient_parse_skips_bad_entries_and_ignores_plain_text_passwords()
    {
        const string json = """
            // hand-edited
            { "machines": [
                { "name": "ok", "host": "srv1", "protocol": "HTTPS" },
                { "name": "ok", "host": "srv2" },
                { "host": "no-name" },
                "not an object",
                { "name": "This computer", "host": "localhost" },
                { "name": "odd", "host": "srv3", "protocol": "ftp",
                  "credential": { "userName": "u", "password": "secret", "protectedPassword": "%%%" } },
            ], }
            """;

        var result = MachineListFile.Parse(json);

        Assert.Null(result.Error);
        Assert.Equal(["ok", "odd"], result.Machines.Select(m => m.Name));
        Assert.Equal(RemoteProtocol.WsManHttps, result.Machines[0].Protocol);
        Assert.Equal(RemoteProtocol.WsMan, result.Machines[1].Protocol);
        Assert.Equal("u", result.Machines[1].Credential.UserName);
        Assert.Null(result.Machines[1].Credential.ProtectedPassword);
        Assert.Equal(4, result.SkippedEntries);
        Assert.True(result.KeepCopyBeforeSaving);
        Assert.Contains(result.Warnings, w => w.Contains("plain-text", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("damaged", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{ not json", "isn't valid JSON")]
    [InlineData("[1, 2]", "must contain a JSON object")]
    [InlineData("{ \"machines\": 5 }", "must be a JSON array")]
    public void Corrupt_files_report_an_error_and_no_machines(string json, string expected)
    {
        var result = MachineListFile.Parse(json);

        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Machines);
        Assert.True(result.KeepCopyBeforeSaving);
    }

    [Fact]
    public void Save_and_load_use_a_file_next_to_the_exe()
    {
        var dir = Directory.CreateTempSubdirectory("cf-machines-");
        try
        {
            var path = MachineListFile.DefaultPath(dir.FullName);
            Assert.False(MachineListFile.Load(path).FileExists);

            MachineListFile.Save(path, [new MachineEntry("a", "10.1.1.1", RemoteProtocol.WsMan, MachineCredential.CurrentAccount)]);
            var loaded = MachineListFile.Load(path);

            Assert.True(loaded.FileExists);
            Assert.Equal("a", Assert.Single(loaded.Machines).Name);
            Assert.Equal(["CryptoFixer.machines.json"], dir.GetFiles().Select(f => f.Name));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(@"CONTOSO\admin", "CONTOSO", "admin")]
    [InlineData("admin@contoso.com", "", "admin@contoso.com")]
    [InlineData(@".\Administrator", ".", "Administrator")]
    [InlineData(" localuser ", "", "localuser")]
    public void SplitUserName_separates_the_domain(string input, string domain, string user) =>
        Assert.Equal((domain, user), MachineCredential.SplitUserName(input));
}

public class MachineValidationTests
{
    private static readonly string[] Existing = ["web01", "db01"];

    [Theory]
    [InlineData("", "host", "Enter a display name.")]
    [InlineData("WEB01", "host", "Another machine already uses that name.")]
    [InlineData("This Computer", "host", "reserved")]
    [InlineData("new", "", "Enter a host name")]
    [InlineData("new", "http://srv", "isn't a valid host")]
    [InlineData("new", "srv:5985", "isn't a valid host")]
    [InlineData("new", "my server", "isn't a valid host")]
    [InlineData("new", "1.2.3", "isn't a valid host")]
    [InlineData("new", "256.1.1.1", "isn't a valid host")]
    public void Invalid_input_is_rejected(string name, string host, string expected) =>
        Assert.Contains(expected, MachineEntry.Validate(name, host, null, true, Existing), StringComparison.Ordinal);

    [Theory]
    [InlineData("srv01")]
    [InlineData("srv01.contoso.com")]
    [InlineData("10.0.0.1")]
    [InlineData("fe80::1")]
    [InlineData("[2001:db8::5]")]
    public void Valid_hosts_are_accepted(string host) =>
        Assert.Null(MachineEntry.Validate("new", host, null, true, Existing));

    [Fact]
    public void Keeping_the_name_while_editing_is_not_a_duplicate()
    {
        Assert.Null(MachineEntry.Validate("Web01", "srv", null, true, Existing, originalName: "web01"));
        Assert.NotNull(MachineEntry.Validate("db01", "srv", null, true, Existing, originalName: "web01"));
    }

    [Fact]
    public void A_user_name_is_required_unless_the_current_account_is_used()
    {
        Assert.Contains("user name", MachineEntry.Validate("new", "srv", " ", false, Existing), StringComparison.Ordinal);
        Assert.Null(MachineEntry.Validate("new", "srv", @"CONTOSO\admin", false, Existing));
    }
}

public class MachineSortingAndFilterTests
{
    private static MachineEntry M(string name, string host) => new(name, host, RemoteProtocol.WsMan, MachineCredential.CurrentAccount);

    [Fact]
    public void Address_sort_is_numeric_ipv4_then_ipv6_then_host_names()
    {
        string[] hosts = ["srv-b", "10.0.0.10", "fe80::2", "9.255.0.1", "10.0.0.9", "SRV-A", "[::1]", "10.0.0.100", "192.168.1.1"];

        var sorted = hosts.Order(HostAddress.Comparer).ToList();

        Assert.Equal(["9.255.0.1", "10.0.0.9", "10.0.0.10", "10.0.0.100", "192.168.1.1", "[::1]", "fe80::2", "SRV-A", "srv-b"], sorted);
    }

    [Fact]
    public void Lookalike_ipv4_text_sorts_with_host_names()
    {
        Assert.Equal(HostAddress.Kind.HostName, HostAddress.Classify("10.1"));
        Assert.Equal(HostAddress.Kind.HostName, HostAddress.Classify("010.0.0.1x"));
        Assert.Equal(HostAddress.Kind.IPv4, HostAddress.Classify(" 10.0.0.1 "));
        Assert.Equal(HostAddress.Kind.IPv6, HostAddress.Classify("[fe80::1%4]"));
        Assert.True(HostAddress.IsIpAddress("2001:db8::1"));
        Assert.False(HostAddress.IsIpAddress("srv01"));
    }

    [Fact]
    public void Sort_by_name_or_address()
    {
        MachineEntry[] machines = [M("zeta", "10.0.0.2"), M("Alpha", "10.0.0.10"), M("beta", "10.0.0.1")];

        Assert.Equal(["Alpha", "beta", "zeta"], MachineListQuery.Sort(machines, MachineSortOrder.Name).Select(m => m.Name));
        Assert.Equal(["beta", "zeta", "Alpha"], MachineListQuery.Sort(machines, MachineSortOrder.Address).Select(m => m.Name));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("  ", true)]
    [InlineData("WEB", true)]
    [InlineData("10.0.0", true)]
    [InlineData("web 10.0", true)]
    [InlineData("web db", false)]
    [InlineData("contoso", false)]
    public void Filter_matches_every_term_against_name_or_host(string? filter, bool expected) =>
        Assert.Equal(expected, MachineListQuery.Matches(M("web01", "10.0.0.5"), filter));
}

public class DpapiSecretProtectorTests
{
    private static SecureString Secure(string text)
    {
        var secure = new SecureString();
        foreach (var c in text)
        {
            secure.AppendChar(c);
        }

        return secure;
    }

    private static string Reveal(SecureString secure) => new System.Net.NetworkCredential(string.Empty, secure).Password;

    [Fact]
    public void Protect_then_unprotect_round_trips_without_storing_plain_text()
    {
        var protector = new DpapiSecretProtector();
        const string password = "P@ssw0rd-äöü-€";

        var blob = protector.Protect(Secure(password));

        Assert.DoesNotContain("P@ssw0rd", System.Text.Encoding.Unicode.GetString(blob), StringComparison.Ordinal);
        var restored = protector.Unprotect(blob);
        Assert.True(restored.IsReadOnly());
        Assert.Equal(password, Reveal(restored));
    }

    [Fact]
    public void Tampered_data_throws_so_the_app_can_ask_again()
    {
        var protector = new DpapiSecretProtector();
        var blob = protector.Protect(Secure("secret"));
        blob[^5] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(blob));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect([1, 2, 3]));
    }
}
