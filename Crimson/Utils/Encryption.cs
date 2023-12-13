using System;
using System.IO;
using System.Security.Cryptography;

namespace Crimson.Utils;

public static class KeyManager
{
    public static byte[] GenerateKey()
    {
        var key = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(key);
        return key;
    }

    public static string EncryptString(string text)
    {
        try
        {
            var key = GenerateKey();

            using var aesAlg = Aes.Create();
            using var encryptor = aesAlg.CreateEncryptor(key, aesAlg.IV);
            using var msEncrypt = new MemoryStream();
            using (var csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
            using (var swEncrypt = new StreamWriter(csEncrypt))
            {
                swEncrypt.Write(text);
            }

            var iv = aesAlg.IV;
            var encryptedContent = msEncrypt.ToArray();

            var result = new byte[iv.Length + encryptedContent.Length];
            Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
            Buffer.BlockCopy(encryptedContent, 0, result, iv.Length, encryptedContent.Length);

            var protectedKey = ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine);

            return Convert.ToBase64String(result) + "|" + Convert.ToBase64String(protectedKey);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
            return null;
        }
    }

    public static string DecryptString(string cipherText)
    {
        var parts = cipherText.Split('|');
        var fullCipher = Convert.FromBase64String(parts[0]);
        var protectedKey = Convert.FromBase64String(parts[1]);

        var key = ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.LocalMachine);

        var iv = new byte[16];
        var cipher = new byte[fullCipher.Length - iv.Length];

        Buffer.BlockCopy(fullCipher, 0, iv, 0, iv.Length);
        Buffer.BlockCopy(fullCipher, iv.Length, cipher, 0, cipher.Length);

        using var aesAlg = Aes.Create();
        using var decryptor = aesAlg.CreateDecryptor(key, iv);
        using MemoryStream msDecrypt = new(cipher);
        using CryptoStream csDecrypt = new(msDecrypt, decryptor, CryptoStreamMode.Read);
        using StreamReader srDecrypt = new(csDecrypt);
        var result = srDecrypt.ReadToEnd();

        return result;
    }
}