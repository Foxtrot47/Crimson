using System.Security.Cryptography;

namespace Crimson.Core
{
    public static class Util
    {
        public static string CalculateSHA1(string filePath)
        {
            using var sha1 = SHA1.Create();
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(sha1.ComputeHash(stream)).ToLowerInvariant();
        }
    }
}

namespace Crimson.Utils
{
    public static class KeyManager
    {
        public static string EncryptString(string value) => throw new InvalidOperationException(
            "Credential encryption is unavailable in headless characterization tests.");

        public static string DecryptString(string value) => throw new InvalidOperationException(
            "Credential encryption is unavailable in headless characterization tests.");
    }
}
