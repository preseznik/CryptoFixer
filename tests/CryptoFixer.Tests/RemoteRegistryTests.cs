using CryptoFixer.Core.Backup;
using CryptoFixer.Core.Registry;
using CryptoFixer.Core.Remote;
using CryptoFixer.Core.Schannel;
using Microsoft.Win32;

namespace CryptoFixer.Tests;

/// <summary>Imitates root\default:StdRegProv (return codes, output names and CIM value types) over an in-memory registry.</summary>
internal sealed class FakeStdRegProv : IStdRegProv
{
    public InMemoryRegistry Registry { get; } = new();

    public List<(string Method, IReadOnlyDictionary<string, object> Inputs)> Calls { get; } = [];

    /// <summary>Methods that fail with the given Win32 code, e.g. 5 for access denied.</summary>
    public Dictionary<string, uint> Failures { get; } = new(StringComparer.Ordinal);

    public StdRegProvResult Invoke(string methodName, IReadOnlyDictionary<string, object> inputs)
    {
        Calls.Add((methodName, inputs));
        Assert.Equal(CimRegistryAccess.HKeyLocalMachine, Assert.IsType<uint>(inputs["hDefKey"]));
        if (Failures.TryGetValue(methodName, out var failure))
        {
            return Result(failure);
        }

        var key = Assert.IsType<string>(inputs["sSubKeyName"]);
        var valueName = inputs.TryGetValue("sValueName", out var v) ? Assert.IsType<string>(v) : null;
        var exists = Registry.KeyExists(key);

        switch (methodName)
        {
            case "EnumKey":
                return exists ? Result(0, ("sNames", NullIfEmpty(Registry.GetSubKeyNames(key).ToArray()))) : Result(2);
            case "EnumValues":
                if (!exists)
                {
                    return Result(2);
                }

                var names = Registry.GetValueNames(key).ToArray();
                var types = names.Select(n => TypeCode(Registry.GetValue(key, n)!.Kind)).ToArray();
                return Result(0, ("sNames", NullIfEmpty(names)), ("Types", names.Length == 0 ? null : types));
            case "CreateKey":
                Registry.CreateKey(key);
                return Result(0);
            case "SetDWORDValue":
                if (!exists)
                {
                    return Result(2);
                }

                Registry.SetDWord(key, valueName!, Assert.IsType<uint>(inputs["uValue"]));
                return Result(0);
            case "DeleteValue":
                return Registry.GetValue(key, valueName!) is null ? Result(2) : Result(Registry.DeleteValue(key, valueName!) ? 0u : 2u);
        }

        var value = Registry.GetValue(key, valueName!);
        if (value is null)
        {
            return Result(exists ? 1u : 2u);
        }

        return (methodName, value.Data) switch
        {
            ("GetDWORDValue", int i) => Result(0, ("uValue", unchecked((uint)i))),
            ("GetQWORDValue", long l) => Result(0, ("uValue", unchecked((ulong)l))),
            ("GetStringValue" or "GetExpandedStringValue", string s) => Result(0, ("sValue", s)),
            ("GetMultiStringValue", string[] m) => Result(0, ("sValue", m)),
            ("GetBinaryValue", byte[] b) => Result(0, ("uValue", b)),
            _ => Result(1),
        };
    }

    private static object? NullIfEmpty(string[] names) => names.Length == 0 ? null : names;

    // Real StdRegProv returns Types as a CIM sint32 array.
    private static int TypeCode(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String => 1,
        RegistryValueKind.ExpandString => 2,
        RegistryValueKind.Binary => 3,
        RegistryValueKind.DWord => 4,
        RegistryValueKind.MultiString => 7,
        RegistryValueKind.QWord => 11,
        _ => 0,
    };

    private static StdRegProvResult Result(uint code, params (string Name, object? Value)[] outputs) =>
        new(code, outputs.ToDictionary(o => o.Name, o => o.Value, StringComparer.OrdinalIgnoreCase));
}

public class CimRegistryAccessTests
{
    private const string Key = @"SOFTWARE\CryptoFixerTest";

    private readonly FakeStdRegProv _provider = new();

    private CimRegistryAccess Create(bool readOnly = false) => new(_provider, "srv01", readOnly);

    [Fact]
    public void Value_types_map_to_the_same_kinds_and_clr_types_as_the_local_registry()
    {
        _provider.Registry.Set(Key, "Dword", RegistryValueData.DWord(0xFFFFFFFF));
        _provider.Registry.Set(Key, "Qword", new RegistryValueData(RegistryValueKind.QWord, 5_000_000_000L));
        _provider.Registry.Set(Key, "Sz", new RegistryValueData(RegistryValueKind.String, "text"));
        _provider.Registry.Set(Key, "Expand", new RegistryValueData(RegistryValueKind.ExpandString, @"C:\Windows"));
        _provider.Registry.Set(Key, "Multi", new RegistryValueData(RegistryValueKind.MultiString, new[] { "a", "b" }));
        _provider.Registry.Set(Key, "Bin", new RegistryValueData(RegistryValueKind.Binary, new byte[] { 1, 2 }));
        _provider.Registry.Set(Key, "None", new RegistryValueData(RegistryValueKind.None, Array.Empty<byte>()));
        var registry = Create();

        Assert.Equal(RegistryValueData.DWord(0xFFFFFFFF), registry.GetValue(Key, "dword"));
        Assert.Equal(new RegistryValueData(RegistryValueKind.QWord, 5_000_000_000L), registry.GetValue(Key, "Qword"));
        Assert.Equal(new RegistryValueData(RegistryValueKind.String, "text"), registry.GetValue(Key, "Sz"));
        Assert.Equal(new RegistryValueData(RegistryValueKind.ExpandString, @"C:\Windows"), registry.GetValue(Key, "Expand"));
        Assert.Equal(["a", "b"], Assert.IsType<string[]>(registry.GetValue(Key, "Multi")!.Data));
        Assert.Equal([1, 2], Assert.IsType<byte[]>(registry.GetValue(Key, "Bin")!.Data));
        Assert.Null(registry.GetValue(Key, "None"));
        Assert.Null(registry.GetValue(Key, "Missing"));
        Assert.Equal(7, registry.GetValueNames(Key).Count);
    }

    [Fact]
    public void Missing_keys_read_as_absent()
    {
        _provider.Registry.CreateKey(@"SOFTWARE\Parent\Child");
        var registry = Create();

        Assert.True(registry.KeyExists(@"\SOFTWARE\Parent\"));
        Assert.False(registry.KeyExists(@"SOFTWARE\Nope"));
        Assert.Equal(["Child"], registry.GetSubKeyNames(@"SOFTWARE\Parent"));
        Assert.Empty(registry.GetSubKeyNames(@"SOFTWARE\Parent\Child"));
        Assert.Empty(registry.GetSubKeyNames(@"SOFTWARE\Nope"));
        Assert.Empty(registry.GetValueNames(@"SOFTWARE\Nope"));
        Assert.Null(registry.GetValue(@"SOFTWARE\Nope", "x"));
        Assert.Equal("HKEY_LOCAL_MACHINE", registry.RootName);
    }

    [Fact]
    public void SetDWord_creates_the_key_and_DeleteValue_reports_missing_values()
    {
        var registry = Create();

        registry.SetDWord(@"SOFTWARE\New\Key", "Enabled", 0xFFFFFFFF);

        Assert.Equal(["CreateKey", "SetDWORDValue"], _provider.Calls.Select(c => c.Method));
        Assert.Equal(0xFFFFFFFFu, registry.GetValue(@"SOFTWARE\New\Key", "Enabled")!.AsUInt32());
        Assert.True(registry.DeleteValue(@"SOFTWARE\New\Key", "Enabled"));
        Assert.False(registry.DeleteValue(@"SOFTWARE\New\Key", "Enabled"));
        Assert.False(registry.DeleteValue(@"SOFTWARE\Nope", "Enabled"));
    }

    [Fact]
    public void Read_only_connections_never_send_writes()
    {
        var registry = Create(readOnly: true);

        Assert.Throws<InvalidOperationException>(() => registry.SetDWord(Key, "Enabled", 1));
        Assert.Throws<InvalidOperationException>(() => registry.DeleteValue(Key, "Enabled"));
        Assert.Empty(_provider.Calls);
        Assert.Throws<InvalidOperationException>(() => new SchannelProtocolStore(registry).Apply(
            [new PendingChange(ProtocolCatalog.Tls10, ProtocolRole.Server, ConfiguredSetting.Disabled)]));
        Assert.Empty(_provider.Calls);
    }

    [Theory]
    [InlineData("EnumValues")]
    [InlineData("CreateKey")]
    [InlineData("SetDWORDValue")]
    public void Access_denied_return_codes_become_UnauthorizedAccessException(string method)
    {
        _provider.Failures[method] = 5;
        var registry = Create();

        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
        {
            registry.GetValue(Key, "Enabled");
            registry.SetDWord(Key, "Enabled", 1);
        });

        Assert.Contains("srv01", ex.Message, StringComparison.Ordinal);
        Assert.Contains(@"HKEY_LOCAL_MACHINE\SOFTWARE\CryptoFixerTest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_return_codes_become_IOException()
    {
        _provider.Failures["EnumKey"] = 87;

        var ex = Assert.Throws<IOException>(() => Create().KeyExists(Key));

        Assert.Contains("EnumKey", ex.Message, StringComparison.Ordinal);
        Assert.Contains("error 87", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Schannel_store_os_detection_and_backup_work_over_StdRegProv()
    {
        _provider.Registry.SetProtocol(ProtocolCatalog.Tls10, ProtocolRole.Server, 0, 1);
        _provider.Registry.SetProtocol(ProtocolCatalog.Tls12, ProtocolRole.Client, 1, 0);
        _provider.Registry.Set(WindowsVersionInfo.CurrentVersionKey, "ProductName", new RegistryValueData(RegistryValueKind.String, "Windows Server 2022 Datacenter"));
        _provider.Registry.Set(WindowsVersionInfo.CurrentVersionKey, "CurrentBuildNumber", new RegistryValueData(RegistryValueKind.String, "20348"));
        _provider.Registry.Set(WindowsVersionInfo.CurrentVersionKey, "UBR", RegistryValueData.DWord(2700));
        _provider.Registry.Set(WindowsVersionInfo.CurrentVersionKey, "InstallationType", new RegistryValueData(RegistryValueKind.String, "Server"));
        var remote = Create();

        var os = WindowsVersionInfo.Read(remote, fallbackBuild: 0);
        var remoteStates = new SchannelProtocolStore(remote).ReadAll(os.Build);
        var directStates = new SchannelProtocolStore(_provider.Registry).ReadAll(os.Build);
        var backup = RegFileExporter.ExportProtocols(remote, DateTimeOffset.Now, "srv01", isRemote: true);

        Assert.Equal((20348, 2700, true), (os.Build, os.Revision, os.IsServer));
        Assert.Equal(directStates, remoteStates);
        Assert.Contains(@"[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.0\Server]", backup, StringComparison.Ordinal);
        Assert.Contains("on remote computer srv01", backup, StringComparison.Ordinal);
        Assert.Contains("Restore ON THAT COMPUTER", backup, StringComparison.Ordinal);

        new SchannelProtocolStore(remote).Apply([new PendingChange(ProtocolCatalog.Tls10, ProtocolRole.Server, ConfiguredSetting.NotConfigured)]);
        Assert.Null(_provider.Registry.GetValue(SchannelPaths.RoleKey(ProtocolCatalog.Tls10, ProtocolRole.Server), "Enabled"));
    }
}

public class RemoteErrorMapperTests
{
    [Theory]
    [InlineData(0x80338012u, RemoteFailureKind.WinRmUnavailable, "Enable-PSRemoting")]
    [InlineData(0x80338126u, RemoteFailureKind.NoResponse, "WinRM (TCP 5985)")]
    [InlineData(0x80070005u, RemoteFailureKind.AccessDenied, "LocalAccountTokenFilterPolicy")]
    [InlineData(0x80041003u, RemoteFailureKind.AccessDenied, "local administrator")]
    [InlineData(0x8007052Eu, RemoteFailureKind.LogonFailure, "user name or password")]
    [InlineData(0x80072EE7u, RemoteFailureKind.NameNotResolved, "couldn't be resolved")]
    [InlineData(0x80041064u, RemoteFailureKind.LocalCredentials, "current Windows account")]
    public void Known_codes_map_to_actionable_messages(uint code, RemoteFailureKind kind, string expected)
    {
        var failure = RemoteErrorMapper.Map(new RemoteFailureInfo(code, "original text"), "srv01", RemoteProtocol.WsMan);

        Assert.Equal(kind, failure.Kind);
        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
        Assert.Equal($"0x{code:X8}: original text", failure.Detail);
    }

    [Fact]
    public void Ip_targets_without_kerberos_point_to_TrustedHosts_or_HTTPS()
    {
        var failure = RemoteErrorMapper.Map(
            new RemoteFailureInfo(null, "HRESULT 0x803380e4 The WinRM client cannot process the request."), "10.0.0.5", RemoteProtocol.WsMan);

        Assert.Equal(RemoteFailureKind.KerberosUnavailable, failure.Kind);
        Assert.Contains("10.0.0.5 is an IP address", failure.Message, StringComparison.Ordinal);
        Assert.Contains("HTTPS", failure.Message, StringComparison.Ordinal);
        Assert.Contains(@"Set-Item WSMan:\localhost\Client\TrustedHosts -Value 10.0.0.5", failure.Message, StringComparison.Ordinal);
        Assert.Contains("doesn't change TrustedHosts", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Https_and_dcom_failures_mention_their_own_ports_and_rules()
    {
        var https = RemoteErrorMapper.Map(new RemoteFailureInfo(0x80338012, null), "srv01", RemoteProtocol.WsManHttps);
        var dcomTimeout = RemoteErrorMapper.Map(new RemoteFailureInfo(0x40004, "Timed out"), "srv01", RemoteProtocol.Dcom);
        var rpc = RemoteErrorMapper.Map(new RemoteFailureInfo(0x800706BA, "The RPC server is unavailable."), "srv01", RemoteProtocol.Dcom);

        Assert.Contains("5986", https.Message, StringComparison.Ordinal);
        Assert.Equal(RemoteFailureKind.NoResponse, dcomTimeout.Kind);
        Assert.Contains("DCOM", dcomTimeout.Message, StringComparison.Ordinal);
        Assert.Equal(RemoteFailureKind.RpcUnavailable, rpc.Kind);
    }

    [Fact]
    public void Codes_are_found_in_text_and_win32_codes_become_hresults()
    {
        Assert.Equal(0x80338126u, RemoteErrorMapper.FindCode(new RemoteFailureInfo(null, "HRESULT 0x80338126")));
        Assert.Equal(0x80070005u, RemoteErrorMapper.FindCode(new RemoteFailureInfo(5, null)));
        Assert.Null(RemoteErrorMapper.FindCode(new RemoteFailureInfo(0, "no code here")));
    }

    [Fact]
    public void Unknown_failures_fall_back_to_text_and_access_denied_flags()
    {
        var denied = RemoteErrorMapper.Map(new RemoteFailureInfo(null, "something", IsAccessDenied: true), "srv01", RemoteProtocol.WsMan);
        var other = RemoteErrorMapper.Map(new RemoteFailureInfo(0x80004005, "Unspecified   error. More text."), "srv01", RemoteProtocol.WsMan);

        Assert.Equal(RemoteFailureKind.AccessDenied, denied.Kind);
        Assert.Equal(RemoteFailureKind.Other, other.Kind);
        Assert.Equal("Couldn't connect to srv01: Unspecified error.", other.Message);
    }
}
