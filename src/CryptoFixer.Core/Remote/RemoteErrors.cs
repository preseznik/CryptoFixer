using System.Globalization;
using System.Text.RegularExpressions;

namespace CryptoFixer.Core.Remote;

public enum RemoteFailureKind
{
    AccessDenied,
    LogonFailure,
    WinRmUnavailable,
    NoResponse,
    KerberosUnavailable,
    NameNotResolved,
    RpcUnavailable,
    Certificate,
    LocalCredentials,
    Other,
}

/// <summary>Transport-neutral description of a failed remote call, extracted from a CimException (or a test).</summary>
/// <param name="Code">HRESULT/Win32 code when known (CIM error_Code).</param>
/// <param name="Message">Original error text; may contain "HRESULT 0x...".</param>
/// <param name="IsAccessDenied">The transport reported access denied without a more specific code.</param>
public sealed record RemoteFailureInfo(uint? Code, string? Message, bool IsAccessDenied = false);

/// <param name="Message">Friendly explanation with the next step to take.</param>
/// <param name="Detail">Technical detail (code and original text) for tooltips and logs. Never contains credentials.</param>
public sealed record RemoteFailure(RemoteFailureKind Kind, string Message, string? Detail);

/// <summary>A remote operation failed; <see cref="Failure"/> explains why in user terms.</summary>
public sealed class RemoteAccessException : Exception
{
    public RemoteAccessException(RemoteFailure failure, Exception? innerException = null)
        : base(failure.Message, innerException)
    {
        Failure = failure;
    }

    public RemoteFailure Failure { get; }
}

/// <summary>Turns WinRM/DCOM/WMI error codes into actionable messages.</summary>
public static partial class RemoteErrorMapper
{
    public const string TrustedHostsCommand = @"Set-Item WSMan:\localhost\Client\TrustedHosts -Value <host> -Concatenate";

    public static RemoteFailure Map(RemoteFailureInfo info, string host, RemoteProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(host);

        var code = FindCode(info);
        var message = info.Message?.Trim();
        var detail = code is { } c
            ? $"0x{c:X8}{(string.IsNullOrEmpty(message) ? string.Empty : ": " + Collapse(message))}"
            : string.IsNullOrEmpty(message) ? null : Collapse(message);
        var isIp = HostAddress.IsIpAddress(host);
        var isWsMan = protocol != RemoteProtocol.Dcom;
        var port = protocol == RemoteProtocol.WsManHttps ? 5986 : 5985;

        RemoteFailure Make(RemoteFailureKind kind, string text) => new(kind, text, detail);

        switch (code)
        {
            case 0x80070005 or 0x80041003:
                return Make(RemoteFailureKind.AccessDenied, AccessDeniedText(host));
            case 0x8007052E or 0x8009030C or 0x80070569 or 0x80070532 or 0x80070533 or 0x80070775:
                return Make(RemoteFailureKind.LogonFailure,
                    $"{host} rejected the sign-in: the user name or password is wrong, or the account is locked, disabled or not allowed to log on remotely.");
            case 0x80041064:
                return Make(RemoteFailureKind.LocalCredentials,
                    "A user name and password can't be used to connect to this computer itself. Use the current Windows account instead.");
            case 0x803380E4 or 0x8033810E:
                return Make(RemoteFailureKind.KerberosUnavailable, KerberosText(host, isIp));
            case 0x80338012 or 0x80338113 or 0x80072EFD when isWsMan:
                return Make(RemoteFailureKind.WinRmUnavailable,
                    protocol == RemoteProtocol.WsManHttps
                        ? $"Couldn't connect to WinRM over HTTPS on {host} (port {port}). The target needs an HTTPS listener with a certificate (winrm quickconfig -transport:https) and a firewall rule for port {port}."
                        : $"Couldn't connect to WinRM on {host} (port {port}). Enable it on the target (Enable-PSRemoting or winrm quickconfig) and allow \"Windows Remote Management (HTTP-In)\" through its firewall.");
            case 0x80338126 or 0x80072EE2 or 0x800705B4 or 0x00040004 or 0x8007274C:
                return Make(RemoteFailureKind.NoResponse,
                    $"{host} didn't respond in time. Check the name or address and the network, and that the firewall on {host} allows {(isWsMan ? $"WinRM (TCP {port})" : "WMI over DCOM (TCP 135 and dynamic RPC ports)")}.");
            case 0x80072EE7 or 0x80072AF9 or 0x80070035:
                return Make(RemoteFailureKind.NameNotResolved,
                    $"The name {host} couldn't be resolved. Check the spelling, use the fully qualified name, or use the IP address.");
            case 0x800706BA:
                return Make(RemoteFailureKind.RpcUnavailable,
                    $"The RPC server on {host} is unavailable. DCOM needs the \"Windows Management Instrumentation (DCOM-In/WMI-In)\" firewall rules on the target, or switch this machine to WinRM.");
        }

        if (info.IsAccessDenied || ContainsAny(message, "access is denied", "access denied"))
        {
            return Make(RemoteFailureKind.AccessDenied, AccessDeniedText(host));
        }

        if (protocol == RemoteProtocol.WsManHttps && ContainsAny(message, "certificate", "SSL"))
        {
            return Make(RemoteFailureKind.Certificate,
                $"The HTTPS certificate of {host} isn't trusted or doesn't match the name. Connect with the name on the certificate and make sure this computer trusts the certificate's issuer.");
        }

        if (ContainsAny(message, "timed out", "timeout"))
        {
            return Make(RemoteFailureKind.NoResponse, $"{host} didn't respond in time. Check the name or address, the network and the firewall on {host}.");
        }

        var summary = string.IsNullOrEmpty(message) ? "unknown error." : FirstSentence(message);
        return Make(RemoteFailureKind.Other, $"Couldn't connect to {host}: {summary}");
    }

    /// <summary>Uses <see cref="RemoteFailureInfo.Code"/> when set, otherwise the first "0x..." number in the message.
    /// Plain Win32 codes are converted to their HRESULT form.</summary>
    public static uint? FindCode(RemoteFailureInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var code = info.Code is > 0 ? info.Code : null;
        if (code is null && info.Message is { } message && HexCode().Match(message) is { Success: true } match
            && uint.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) && parsed != 0)
        {
            code = parsed;
        }

        return code is < 0x10000 and > 0 ? 0x80070000 | code.Value : code;
    }

    private static string AccessDeniedText(string host) =>
        $"Access denied on {host}. Connect with an account that is a local administrator there. Local (non-domain) accounts are also " +
        "blocked by remote UAC unless LocalAccountTokenFilterPolicy is set to 1 on the target.";

    private static string KerberosText(string host, bool isIp) =>
        (isIp
            ? $"{host} is an IP address, so Kerberos can't be used and WinRM refuses NTLM to unknown hosts. "
            : $"Kerberos can't be used for {host} (it isn't in the domain, the name isn't the computer's real name, or this computer isn't domain-joined). ")
        + "Connect by domain host name, switch to WinRM HTTPS (needs a certificate on the target), or add the target to the WinRM TrustedHosts list on this computer "
        + $"({TrustedHostsCommand.Replace("<host>", host, StringComparison.Ordinal)}) and use a user name and password. CryptoFixer doesn't change TrustedHosts for you.";

    private static bool ContainsAny(string? text, params string[] needles) =>
        text is not null && needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string Collapse(string text) => WhiteSpace().Replace(text, " ").Trim();

    private static string FirstSentence(string text)
    {
        var collapsed = Collapse(text);
        var end = collapsed.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? collapsed[..(end + 1)] : collapsed;
    }

    [GeneratedRegex(@"0x([0-9A-Fa-f]{1,8})\b")]
    private static partial Regex HexCode();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
