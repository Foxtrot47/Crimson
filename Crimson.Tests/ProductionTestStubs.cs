using System.Security.Cryptography;
using System.Text;

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

namespace Crimson.Tests
{
    internal sealed class TestCredentialProtector : Crimson.Core.ICredentialProtector
    {
        private const string Prefix = "test-protected:";

        public string Protect(string value) =>
            Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

        public string Unprotect(string protectedValue)
        {
            if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
                return protectedValue;

            return Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[Prefix.Length..]));
        }
    }
}
