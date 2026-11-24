using CryptoFixer.Core.Registry;

namespace CryptoFixer.Core.Schannel;

public sealed record WindowsVersionInfo(string ProductName, string? DisplayVersion, int Build, int Revision, bool IsServer)
{
    public const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public string Summary =>
        $"{ProductName}{(string.IsNullOrEmpty(DisplayVersion) ? string.Empty : " " + DisplayVersion)} (build {Build}.{Revision})";

    public static WindowsVersionInfo Read(IRegistryAccess registry, int fallbackBuild)
    {
        ArgumentNullException.ThrowIfNull(registry);

        string? GetString(string name) => registry.GetValue(CurrentVersionKey, name)?.Data as string;

        var product = GetString("ProductName") ?? "Windows";
        var displayVersion = GetString("DisplayVersion") ?? GetString("ReleaseId");
        var build = int.TryParse(GetString("CurrentBuildNumber") ?? GetString("CurrentBuild"), out var b) ? b : fallbackBuild;
        var revision = (int)(registry.GetValue(CurrentVersionKey, "UBR")?.AsUInt32() ?? 0);
        var installationType = GetString("InstallationType");
        var isServer = installationType?.StartsWith("Server", StringComparison.OrdinalIgnoreCase) == true
            || product.Contains("Server", StringComparison.OrdinalIgnoreCase);

        // ProductName still reads "Windows 10 ..." on Windows 11 client builds.
        if (!isServer && build >= 22000 && product.StartsWith("Windows 10", StringComparison.Ordinal))
        {
            product = "Windows 11" + product["Windows 10".Length..];
        }

        return new WindowsVersionInfo(product, displayVersion, build, revision, isServer);
    }
}
