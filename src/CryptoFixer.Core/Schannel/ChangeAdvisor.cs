namespace CryptoFixer.Core.Schannel;

/// <summary>Produces human-readable warnings about staged changes before they are applied.</summary>
public static class ChangeAdvisor
{
    public static IReadOnlyList<string> GetWarnings(IReadOnlyList<ProtocolRoleState> current, IReadOnlyList<PendingChange> changes)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changes);

        var projected = Project(current, changes);
        var warnings = new List<string>();

        foreach (var change in changes.Where(c => c.Target == ConfiguredSetting.Enabled))
        {
            var state = current.FirstOrDefault(s => s.Protocol == change.Protocol && s.Role == change.Role);
            if (state?.OsDefault == OsDefaultState.NotSupported)
            {
                warnings.Add($"{change.Protocol} ({change.Role}) is not supported on this Windows build. Enabling it has no effect or is not a safe configuration.");
            }
            else if (change.Protocol.IsDeprecated)
            {
                warnings.Add($"Enabling {change.Protocol} ({change.Role}) weakens security: the protocol is deprecated.");
            }
        }

        foreach (var role in new[] { ProtocolRole.Server, ProtocolRole.Client })
        {
            var modernUsable = projected.Any(s =>
                s.Role == role
                && (s.Protocol == ProtocolCatalog.Tls12 || s.Protocol == ProtocolCatalog.Tls13)
                && ProtocolStateEvaluator.IsUsable(s.Effective));
            var touchesRole = changes.Any(c => c.Role == role);

            if (touchesRole && !modernUsable)
            {
                warnings.Add($"No TLS 1.2 or TLS 1.3 would be enabled for the {role} role. Remote Desktop, WinRM, HTTPS and most other secure connections would fail.");
            }
        }

        var disablesLegacyTls = changes.Any(c =>
            (c.Protocol == ProtocolCatalog.Tls10 || c.Protocol == ProtocolCatalog.Tls11)
            && c.Target == ConfiguredSetting.Disabled
            && current.Any(s => s.Protocol == c.Protocol && s.Role == c.Role && ProtocolStateEvaluator.IsUsable(s.Effective)));
        if (disablesLegacyTls)
        {
            warnings.Add("Disabling TLS 1.0/1.1 can break older clients and applications (e.g. .NET Framework apps without SchUseStrongCrypto/SystemDefaultTlsVersions, old SQL Server or SQL drivers). Verify compatibility first.");
        }

        return warnings;
    }

    /// <summary>Returns the states as they would be after applying <paramref name="changes"/>.</summary>
    public static IReadOnlyList<ProtocolRoleState> Project(IReadOnlyList<ProtocolRoleState> current, IReadOnlyList<PendingChange> changes) =>
        current.Select(s =>
        {
            var change = changes.LastOrDefault(c => c.Protocol == s.Protocol && c.Role == s.Role);
            return change is null ? s : s.With(change.Target);
        }).ToList();

    /// <summary>Warnings for staged cipher, hash and key exchange changes.</summary>
    public static IReadOnlyList<string> GetWarnings(IReadOnlyList<AlgorithmState> current, IReadOnlyList<AlgorithmChange> changes)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changes);

        var warnings = new List<string>();
        if (changes.Count == 0)
        {
            return warnings;
        }

        var projected = Project(current, changes);
        bool Usable(SchannelAlgorithm a) => projected.Any(s => s.Algorithm == a && s.Effective == EffectiveState.Enabled);
        bool WasUsable(SchannelAlgorithm a) => current.Any(s => s.Algorithm == a && s.Effective == EffectiveState.Enabled);
        bool Touches(AlgorithmCategory category) => changes.Any(c => c.Algorithm.Category == category);

        var weakEnabled = changes
            .Where(c => c.Target == ConfiguredSetting.Enabled && c.Algorithm.IsDeprecated)
            .Select(c => c.Algorithm.KeyName)
            .ToList();
        if (weakEnabled.Count > 0)
        {
            warnings.Add($"Enabling {string.Join(", ", weakEnabled)} weakens security: {(weakEnabled.Count == 1 ? "it is" : "they are")} deprecated.");
        }

        if (Touches(AlgorithmCategory.Cipher) && !Usable(AlgorithmCatalog.Aes128) && !Usable(AlgorithmCatalog.Aes256))
        {
            warnings.Add("Both AES ciphers would be disabled. Every modern TLS 1.2 cipher suite uses AES, so most secure connections would fail.");
        }

        if (WasUsable(AlgorithmCatalog.Sha1) && !Usable(AlgorithmCatalog.Sha1))
        {
            warnings.Add("Disabling SHA (SHA-1) blocks every *_SHA cipher suite (e.g. ECDHE_RSA_WITH_AES_256_CBC_SHA) and can break TLS 1.0 / 1.1 and older clients.");
        }

        if (WasUsable(AlgorithmCatalog.Pkcs) && !Usable(AlgorithmCatalog.Pkcs))
        {
            warnings.Add("Disabling PKCS blocks RSA key exchange (TLS_RSA_WITH_* suites) and can break servers that use RSA certificates. Test before rolling it out.");
        }

        if (Touches(AlgorithmCategory.KeyExchange) && AlgorithmCatalog.KeyExchanges.All(a => !Usable(a)))
        {
            warnings.Add("No key exchange algorithm would be enabled. TLS 1.2 connections would fail.");
        }
        else if (WasUsable(AlgorithmCatalog.Ecdh) && !Usable(AlgorithmCatalog.Ecdh))
        {
            warnings.Add("Disabling ECDH blocks the ECDHE cipher suites, the preferred forward-secret suites on every current Windows version.");
        }

        return warnings;
    }

    public static IReadOnlyList<AlgorithmState> Project(IReadOnlyList<AlgorithmState> current, IReadOnlyList<AlgorithmChange> changes) =>
        current.Select(s =>
        {
            var change = changes.LastOrDefault(c => c.Algorithm == s.Algorithm);
            return change is null ? s : s.With(change.Target);
        }).ToList();
}
