using System.Collections;
using System.ComponentModel;
using CryptoFixer.Core.Registry;
using Microsoft.Win32;

namespace CryptoFixer.Core.Remote;

/// <summary>Result of a WMI StdRegProv method call.</summary>
public sealed record StdRegProvResult(uint ReturnValue, IReadOnlyDictionary<string, object?> Outputs)
{
    public object? Output(string name) => Outputs.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Calls methods of the WMI class root\default:StdRegProv (on a remote computer, or a fake in tests).</summary>
public interface IStdRegProv
{
    /// <param name="inputs">Input parameters; values are <see cref="uint"/> or <see cref="string"/>.</param>
    StdRegProvResult Invoke(string methodName, IReadOnlyDictionary<string, object> inputs);
}

/// <summary>
/// <see cref="IRegistryAccess"/> for HKEY_LOCAL_MACHINE on a remote computer, implemented with StdRegProv
/// (EnumKey, EnumValues, Get*Value, CreateKey, SetDWORDValue, DeleteValue).
/// </summary>
public sealed class CimRegistryAccess : IRegistryAccess
{
    public const uint HKeyLocalMachine = 0x80000002;

    private const uint Success = 0;
    private const uint NotFound = 2;
    private const uint AccessDenied = 5;

    private const int RegSz = 1;
    private const int RegExpandSz = 2;
    private const int RegBinary = 3;
    private const int RegDword = 4;
    private const int RegMultiSz = 7;
    private const int RegQword = 11;

    private readonly IStdRegProv _provider;
    private readonly string _host;

    /// <param name="host">Used in error messages only.</param>
    /// <param name="readOnly">When true, every write attempt throws before anything is sent to the computer.</param>
    public CimRegistryAccess(IStdRegProv provider, string host, bool readOnly)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrEmpty(host);
        _provider = provider;
        _host = host;
        IsReadOnly = readOnly;
    }

    public bool IsReadOnly { get; }

    /// <summary>The remote root is HKLM, so exported .reg files import on the target as is.</summary>
    public string RootName => "HKEY_LOCAL_MACHINE";

    public bool KeyExists(string keyPath)
    {
        var result = Call("EnumKey", keyPath);
        return result.ReturnValue switch
        {
            Success => true,
            NotFound => false,
            var code => throw Failure("EnumKey", keyPath, code),
        };
    }

    public IReadOnlyList<string> GetSubKeyNames(string keyPath) => EnumNames("EnumKey", keyPath);

    public IReadOnlyList<string> GetValueNames(string keyPath) => EnumNames("EnumValues", keyPath);

    public RegistryValueData? GetValue(string keyPath, string valueName)
    {
        ArgumentNullException.ThrowIfNull(valueName);

        var listing = Call("EnumValues", keyPath);
        if (listing.ReturnValue == NotFound)
        {
            return null;
        }

        if (listing.ReturnValue != Success)
        {
            throw Failure("EnumValues", keyPath, listing.ReturnValue);
        }

        var names = ToStrings(listing.Output("sNames"));
        var types = ToInts(listing.Output("Types"));
        var index = names.FindIndex(n => string.Equals(n, valueName, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index >= types.Count)
        {
            return null;
        }

        var (method, kind) = types[index] switch
        {
            RegSz => ("GetStringValue", RegistryValueKind.String),
            RegExpandSz => ("GetExpandedStringValue", RegistryValueKind.ExpandString),
            RegBinary => ("GetBinaryValue", RegistryValueKind.Binary),
            RegDword => ("GetDWORDValue", RegistryValueKind.DWord),
            RegMultiSz => ("GetMultiStringValue", RegistryValueKind.MultiString),
            RegQword => ("GetQWORDValue", RegistryValueKind.QWord),
            _ => (null, RegistryValueKind.Unknown),
        };

        if (method is null)
        {
            return null;
        }

        var result = Call(method, keyPath, valueName);
        if (result.ReturnValue == NotFound)
        {
            return null;
        }

        if (result.ReturnValue != Success)
        {
            throw Failure(method, keyPath, result.ReturnValue);
        }

        object? data = kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => result.Output("sValue") as string,
            RegistryValueKind.MultiString => ToStrings(result.Output("sValue")).ToArray(),
            RegistryValueKind.Binary => ToBytes(result.Output("uValue")),
            RegistryValueKind.DWord => result.Output("uValue") is { } d ? unchecked((int)Convert.ToUInt32(d)) : null,
            RegistryValueKind.QWord => result.Output("uValue") is { } q ? unchecked((long)Convert.ToUInt64(q)) : null,
            _ => null,
        };

        return data is null ? null : new RegistryValueData(kind, data);
    }

    public void SetDWord(string keyPath, string valueName, uint value)
    {
        ArgumentNullException.ThrowIfNull(valueName);
        EnsureWritable();

        var created = Call("CreateKey", keyPath);
        if (created.ReturnValue != Success)
        {
            throw Failure("CreateKey", keyPath, created.ReturnValue);
        }

        var inputs = Inputs(keyPath, valueName);
        inputs["uValue"] = value;
        var result = _provider.Invoke("SetDWORDValue", inputs);
        if (result.ReturnValue != Success)
        {
            throw Failure("SetDWORDValue", keyPath, result.ReturnValue);
        }
    }

    public bool DeleteValue(string keyPath, string valueName)
    {
        ArgumentNullException.ThrowIfNull(valueName);
        EnsureWritable();

        var result = Call("DeleteValue", keyPath, valueName);
        return result.ReturnValue switch
        {
            Success => true,
            NotFound => false,
            var code => throw Failure("DeleteValue", keyPath, code),
        };
    }

    private IReadOnlyList<string> EnumNames(string method, string keyPath)
    {
        var result = Call(method, keyPath);
        return result.ReturnValue switch
        {
            Success => ToStrings(result.Output("sNames")),
            NotFound => [],
            var code => throw Failure(method, keyPath, code),
        };
    }

    private StdRegProvResult Call(string method, string keyPath, string? valueName = null) =>
        _provider.Invoke(method, Inputs(keyPath, valueName));

    private static Dictionary<string, object> Inputs(string keyPath, string? valueName)
    {
        ArgumentNullException.ThrowIfNull(keyPath);

        var inputs = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["hDefKey"] = HKeyLocalMachine,
            ["sSubKeyName"] = keyPath.Trim('\\'),
        };

        if (valueName is not null)
        {
            inputs["sValueName"] = valueName;
        }

        return inputs;
    }

    private void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException($"The connection to {_host} is read-only; registry writes are blocked.");
        }
    }

    private Exception Failure(string method, string keyPath, uint code)
    {
        var key = $@"HKEY_LOCAL_MACHINE\{keyPath.Trim('\\')}";
        if (code == AccessDenied)
        {
            return new UnauthorizedAccessException($"Access to {key} on {_host} was denied. Use an account that is a local administrator on {_host}.");
        }

        var reason = code <= int.MaxValue ? new Win32Exception((int)code).Message : $"0x{code:X8}";
        return new IOException($"{method} failed for {key} on {_host}: {reason} (error {code}).");
    }

    private static List<string> ToStrings(object? value) => value switch
    {
        null => [],
        string single => [single],
        IEnumerable items => items.Cast<object?>().Select(item => item?.ToString() ?? string.Empty).ToList(),
        _ => [],
    };

    private static List<int> ToInts(object? value) => value switch
    {
        null => [],
        IEnumerable items => items.Cast<object?>().Select(item => item is null ? 0 : Convert.ToInt32(item)).ToList(),
        _ => [Convert.ToInt32(value)],
    };

    private static byte[]? ToBytes(object? value) => value switch
    {
        null => null,
        byte[] bytes => bytes,
        IEnumerable items => items.Cast<object?>().Select(item => Convert.ToByte(item)).ToArray(),
        _ => null,
    };
}
