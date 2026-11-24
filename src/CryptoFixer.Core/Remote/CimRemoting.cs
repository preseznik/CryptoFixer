using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using CryptoFixer.Core.Registry;
using CryptoFixer.Core.Schannel;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Microsoft.Win32.SafeHandles;

namespace CryptoFixer.Core.Remote;

/// <summary>An open connection to a remote computer.</summary>
public interface IRemoteSession : IDisposable
{
    string Host { get; }

    /// <summary>HKEY_LOCAL_MACHINE on the remote computer.</summary>
    IRegistryAccess Registry { get; }

    WindowsVersionInfo Os { get; }

    /// <summary>Restarts the remote computer after a short delay. Throws on a read-only connection.</summary>
    void Restart(string comment);
}

public interface IRemoteConnector
{
    /// <summary>Connects and reads the OS version. Throws <see cref="RemoteAccessException"/> with an explanation on failure.</summary>
    /// <param name="password">Required when the machine uses a user name; ignored for the current Windows account.</param>
    IRemoteSession Connect(MachineEntry machine, SecureString? password, bool readOnly, CancellationToken cancellationToken);
}

/// <summary>Connects with CIM (Microsoft.Management.Infrastructure) over WinRM or DCOM.</summary>
public sealed class CimRemoteConnector : IRemoteConnector
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly TimeSpan _timeout;

    public CimRemoteConnector(TimeSpan? timeout = null) => _timeout = timeout ?? DefaultTimeout;

    public IRemoteSession Connect(MachineEntry machine, SecureString? password, bool readOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);

        CimSessionOptions options = machine.Protocol == RemoteProtocol.Dcom
            ? new DComSessionOptions()
            : new WSManSessionOptions { UseSsl = machine.Protocol == RemoteProtocol.WsManHttps };
        options.Timeout = _timeout;

        if (!machine.Credential.UsesCurrentAccount)
        {
            if (password is null)
            {
                throw new ArgumentException("A password is required for this machine.", nameof(password));
            }

            var (domain, user) = MachineCredential.SplitUserName(machine.Credential.UserName!);
            options.AddDestinationCredentials(new CimCredential(PasswordAuthenticationMechanism.Default, domain, user, password));
        }

        CimSession session;
        try
        {
            session = CimSession.Create(machine.Host.Trim(), options);
        }
        catch (CimException ex)
        {
            throw CimErrors.Translate(ex, machine.Host, machine.Protocol);
        }

        try
        {
            var provider = new CimStdRegProv(session, machine.Host, machine.Protocol, _timeout, cancellationToken);
            var registry = new CimRegistryAccess(provider, machine.Host, readOnly);
            registry.KeyExists(SchannelPaths.Schannel);
            var os = WindowsVersionInfo.Read(registry, fallbackBuild: 0);
            return new CimRemoteSession(session, machine, registry, os, readOnly, _timeout);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }
}

internal sealed class CimRemoteSession : IRemoteSession
{
    private const string CimV2 = @"root\cimv2";

    // SHTDN_REASON_FLAG_PLANNED | SHTDN_REASON_MAJOR_OPERATINGSYSTEM | SHTDN_REASON_MINOR_RECONFIG, like "shutdown /d p:2:4".
    private const uint PlannedReconfigReason = 0x80020004;

    // EWX_REBOOT | EWX_FORCE, matching "shutdown /r /t 10" (a non-zero delay implies /f).
    private const int ForcedReboot = 6;

    private readonly CimSession _session;
    private readonly MachineEntry _machine;
    private readonly bool _readOnly;
    private readonly TimeSpan _timeout;

    public CimRemoteSession(CimSession session, MachineEntry machine, IRegistryAccess registry, WindowsVersionInfo os, bool readOnly, TimeSpan timeout)
    {
        _session = session;
        _machine = machine;
        Registry = registry;
        Os = os;
        _readOnly = readOnly;
        _timeout = timeout;
    }

    public string Host => _machine.Host;

    public IRegistryAccess Registry { get; }

    public WindowsVersionInfo Os { get; }

    public void Restart(string comment)
    {
        if (_readOnly)
        {
            throw new InvalidOperationException($"The connection to {Host} is read-only; restarting is blocked.");
        }

        Privileges.TryEnable("SeRemoteShutdownPrivilege");
        Privileges.TryEnable("SeShutdownPrivilege");

        using var options = new CimOperationOptions { Timeout = _timeout };
        try
        {
            var os = _session.QueryInstances(CimV2, "WQL", "SELECT * FROM Win32_OperatingSystem", options).FirstOrDefault()
                ?? throw new IOException($"{Host} didn't return its Win32_OperatingSystem instance.");
            using (os)
            {
                using var parameters = new CimMethodParametersCollection
                {
                    CimMethodParameter.Create("Timeout", 10u, CimType.UInt32, CimFlags.None),
                    CimMethodParameter.Create("Comment", comment, CimType.String, CimFlags.None),
                    CimMethodParameter.Create("ReasonCode", PlannedReconfigReason, CimType.UInt32, CimFlags.None),
                    CimMethodParameter.Create("Flags", ForcedReboot, CimType.SInt32, CimFlags.None),
                };
                var result = _session.InvokeMethod(CimV2, os, "Win32ShutdownTracker", parameters, options);
                var code = Convert.ToUInt32(result.ReturnValue?.Value ?? 0u);
                if (code != 0)
                {
                    var reason = code <= int.MaxValue ? new Win32Exception((int)code).Message : $"0x{code:X8}";
                    throw new IOException($"{Host} refused the restart: {reason} (error {code}).");
                }
            }
        }
        catch (CimException ex)
        {
            throw CimErrors.Translate(ex, Host, _machine.Protocol);
        }
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>Invokes StdRegProv methods over an open <see cref="CimSession"/>.</summary>
internal sealed class CimStdRegProv : IStdRegProv
{
    private const string Namespace = @"root\default";
    private const string ClassName = "StdRegProv";

    private readonly CimSession _session;
    private readonly string _host;
    private readonly RemoteProtocol _protocol;
    private readonly TimeSpan _timeout;
    private readonly CancellationToken _cancellationToken;

    public CimStdRegProv(CimSession session, string host, RemoteProtocol protocol, TimeSpan timeout, CancellationToken cancellationToken)
    {
        _session = session;
        _host = host;
        _protocol = protocol;
        _timeout = timeout;
        _cancellationToken = cancellationToken;
    }

    public StdRegProvResult Invoke(string methodName, IReadOnlyDictionary<string, object> inputs)
    {
        using var parameters = new CimMethodParametersCollection();
        foreach (var (name, value) in inputs)
        {
            var type = value switch
            {
                uint => CimType.UInt32,
                int => CimType.SInt32,
                ulong => CimType.UInt64,
                string => CimType.String,
                _ => throw new ArgumentException($"Unsupported StdRegProv parameter type {value.GetType().Name}.", nameof(inputs)),
            };
            parameters.Add(CimMethodParameter.Create(name, value, type, CimFlags.None));
        }

        using var options = new CimOperationOptions { Timeout = _timeout };
        if (_cancellationToken.CanBeCanceled)
        {
            options.CancellationToken = _cancellationToken;
        }

        CimMethodResult result;
        try
        {
            result = _session.InvokeMethod(Namespace, ClassName, methodName, parameters, options);
        }
        catch (CimException ex)
        {
            throw CimErrors.Translate(ex, _host, _protocol);
        }

        var outputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in result.OutParameters)
        {
            outputs[parameter.Name] = parameter.Value;
        }

        return new StdRegProvResult(Convert.ToUInt32(result.ReturnValue?.Value ?? 0u), outputs);
    }
}

internal static class CimErrors
{
    public static RemoteAccessException Translate(CimException ex, string host, RemoteProtocol protocol)
    {
        uint? code = null;
        try
        {
            if (ex.ErrorData?.CimInstanceProperties["error_Code"]?.Value is { } value)
            {
                code = Convert.ToUInt32(value);
            }
        }
        catch (Exception readError) when (readError is CimException or ObjectDisposedException or InvalidCastException or OverflowException)
        {
            // Fall back to the code in MessageId/Message.
        }

        var info = new RemoteFailureInfo(code, $"{ex.MessageId} {ex.Message}".Trim(), ex.NativeErrorCode == NativeErrorCode.AccessDenied);
        return new RemoteAccessException(RemoteErrorMapper.Map(info, host, protocol), ex);
    }
}

/// <summary>Enables a privilege in this process's token (needed for WMI remote shutdown over DCOM). Best effort.</summary>
internal static class Privileges
{
    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x8;
    private const uint SePrivilegeEnabled = 0x2;

    public static bool TryEnable(string privilege)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return false;
        }

        using (token)
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid))
            {
                return false;
            }

            var state = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            return AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastPInvokeError() == 0;
        }
    }

    // TOKEN_PRIVILEGES with one LUID_AND_ATTRIBUTES; LUID is two DWORDs, so the struct is 4-byte packed.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);
}
