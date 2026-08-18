using System.Security.Cryptography;
using Crimson.Core;

namespace Crimson.Platform.Windows;

public sealed class WindowsCredentialProtector : ICredentialProtector
{
    public string Protect(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var key = RandomNumberGenerator.GetBytes(32);
        using var aes = Aes.Create();
        using var encryptor = aes.CreateEncryptor(key, aes.IV);
        using var encryptedStream = new MemoryStream();
        using (var cryptoStream = new CryptoStream(encryptedStream, encryptor, CryptoStreamMode.Write))
        using (var writer = new StreamWriter(cryptoStream))
            writer.Write(value);

        var encryptedContent = encryptedStream.ToArray();
        var payload = new byte[aes.IV.Length + encryptedContent.Length];
        Buffer.BlockCopy(aes.IV, 0, payload, 0, aes.IV.Length);
        Buffer.BlockCopy(encryptedContent, 0, payload, aes.IV.Length, encryptedContent.Length);
        var protectedKey = ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(payload) + "|" + Convert.ToBase64String(protectedKey);
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        var parts = protectedValue.Split('|');
        if (parts.Length != 2)
            throw new CryptographicException("Protected credential has an invalid format.");

        var payload = Convert.FromBase64String(parts[0]);
        if (payload.Length < 16)
            throw new CryptographicException("Protected credential payload is too short.");
        var protectedKey = Convert.FromBase64String(parts[1]);
        var key = ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.LocalMachine);
        var iv = payload.AsSpan(0, 16).ToArray();
        var cipher = payload.AsSpan(16).ToArray();

        using var aes = Aes.Create();
        using var decryptor = aes.CreateDecryptor(key, iv);
        using var encryptedStream = new MemoryStream(cipher);
        using var cryptoStream = new CryptoStream(encryptedStream, decryptor, CryptoStreamMode.Read);
        using var reader = new StreamReader(cryptoStream);
        return reader.ReadToEnd();
    }
}
