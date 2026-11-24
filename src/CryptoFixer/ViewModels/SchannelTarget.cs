using System.IO;
using CryptoFixer.Core.Remote;
using CryptoFixer.Core.Schannel;

namespace CryptoFixer.ViewModels;

/// <summary>Remote-machine services and policy for the main view.</summary>
/// <param name="MachinesPath">The portable machine list next to the executable.</param>
/// <param name="AllowWrites">False when started with --read-only: remote connections are then read-only too.</param>
/// <param name="ReadOnlyReason">Shown when <paramref name="AllowWrites"/> is false.</param>
public sealed record RemoteSetup(string MachinesPath, IRemoteConnector Connector, ISecretProtector Protector, bool AllowWrites, string ReadOnlyReason);

/// <summary>The computer whose Schannel settings are shown: the local registry or an open remote session.</summary>
public sealed class SchannelTarget : IDisposable
{
    private readonly CancellationTokenSource? _cancellation;

    private SchannelTarget(
        string displayName,
        string hostName,
        SchannelProtocolStore store,
        WindowsVersionInfo os,
        bool canWrite,
        string readOnlyReason,
        IRemoteSession? session,
        CancellationTokenSource? cancellation)
    {
        DisplayName = displayName;
        HostName = hostName;
        Store = store;
        Os = os;
        CanWrite = canWrite;
        ReadOnlyReason = readOnlyReason;
        Session = session;
        _cancellation = cancellation;
    }

    public string DisplayName { get; }

    /// <summary>Computer name (local) or the host name / IP address used to connect (remote).</summary>
    public string HostName { get; }

    public SchannelProtocolStore Store { get; }

    public WindowsVersionInfo Os { get; }

    public bool CanWrite { get; }

    public string ReadOnlyReason { get; }

    public IRemoteSession? Session { get; }

    public bool IsLocal => Session is null;

    public string RegistryPath => IsLocal
        ? $@"HKEY_LOCAL_MACHINE\{SchannelPaths.Protocols}"
        : $@"\\{HostName}\HKEY_LOCAL_MACHINE\{SchannelPaths.Protocols}";

    /// <summary>Host name usable in a file name (IPv6 colons and other reserved characters become '-').</summary>
    public string FileNameHost => SafeFileName(HostName);

    public static SchannelTarget Local(SchannelProtocolStore store, WindowsVersionInfo os, bool canWrite, string readOnlyReason) =>
        new(Core.Remote.MachineListFile.ThisComputerName, Environment.MachineName, store, os, canWrite, readOnlyReason, null, null);

    public static SchannelTarget Remote(MachineEntry machine, IRemoteSession session, CancellationTokenSource cancellation, bool canWrite, string readOnlyReason) =>
        new(machine.Name, machine.Host, new SchannelProtocolStore(session.Registry), session.Os, canWrite, readOnlyReason, session, cancellation);

    public static string SafeFileName(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = text.Trim().Select(c => invalid.Contains(c) || c is '%' or '[' or ']' ? '-' : c).ToArray();
        return new string(chars).Trim('-', '.') is { Length: > 0 } safe ? safe : "remote";
    }

    public void Dispose()
    {
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            _cancellation.Dispose();
        }

        Session?.Dispose();
    }
}
