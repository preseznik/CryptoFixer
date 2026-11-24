using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace CryptoFixer.Core.Remote;

/// <summary>Encrypts saved passwords. <see cref="Unprotect"/> throws <see cref="CryptographicException"/> when the data can't be decrypted.</summary>
public interface ISecretProtector
{
    byte[] Protect(SecureString secret);

    SecureString Unprotect(byte[] protectedData);
}

/// <summary>
/// Windows DPAPI with <see cref="DataProtectionScope.CurrentUser"/>: only the same Windows user on the same computer
/// (or a roaming profile of that user) can decrypt the data, so a copied machines file never reveals passwords.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CryptoFixer.machines.v1");

    public byte[] Protect(SecureString secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        var plain = new byte[secret.Length * sizeof(char)];
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(secret);
        try
        {
            Marshal.Copy(pointer, plain, 0, plain.Length);
            return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(pointer);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public SecureString Unprotect(byte[] protectedData)
    {
        ArgumentNullException.ThrowIfNull(protectedData);

        var plain = ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            if (plain.Length % sizeof(char) != 0)
            {
                throw new CryptographicException("The saved password data is damaged.");
            }

            var chars = MemoryMarshal.Cast<byte, char>(plain);
            var secret = new SecureString();
            foreach (var c in chars)
            {
                secret.AppendChar(c);
            }

            secret.MakeReadOnly();
            return secret;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}
