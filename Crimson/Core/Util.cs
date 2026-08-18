using System;
using System.IO;
using System.Security.Cryptography;

namespace Crimson.Core
{
    public class Util
    {

        public static string ConvertMiBToGiBOrMiB(double mib)
        {
            var gib = mib / 1024;

            return gib >= 1 ? $"{gib:F2} GiB" : $"{mib:F2} MiB";
        }

        public static string CalculateSHA1(string filePath)
        {
            using var sha1 = SHA1.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha1.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        public static string CalculateSHA1(byte[] data)
        {
            var hashBytes = SHA1.HashData(data);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }
}
