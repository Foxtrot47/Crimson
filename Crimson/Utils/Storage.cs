using Crimson.Core;
using Crimson.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace Crimson.Utils
{
    internal class Storage
    {
        private static readonly string UserDataFile = $@"{ApplicationData.Current.LocalFolder.Path}\user.json";
        public static async Task<UserData> GetUserData()
        {
            if (!File.Exists(UserDataFile))
            {
                await SaveUserData(null);
                return null;
            }
            await using var fileStream = File.Open(UserDataFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var streamReader = new StreamReader(fileStream);
            var jsonString = await streamReader.ReadToEndAsync();
            var userData = JsonSerializer.Deserialize<UserData>(jsonString);

            return userData;
        }
        public static async Task SaveUserData(UserData data)
        {
            var jsonString = JsonSerializer.Serialize(data);

            await using var fileStream = File.Open(UserDataFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var streamWriter = new StreamWriter(fileStream);
            await streamWriter.WriteAsync(jsonString);
            await streamWriter.FlushAsync();
        }
    }
}
