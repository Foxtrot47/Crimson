using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;

namespace Crimson.Providers.Ubisoft;

/// <summary>What Crimson needs to reconnect an account without asking. Never logged, never in memory longer than a sign-in.</summary>
public sealed record UbisoftSignInCredential(string Email, string Password);

/// <summary>
/// Stores the sign-in needed for silent reconnection when the person asked to stay signed in.
/// Ubisoft's ticket expires after about three hours and its remember-me renewal rejects
/// third-party clients, so a stored sign-in is the only way to keep a session alive across
/// launches - the same trade the official client makes. The file is protected as the current
/// Windows user and is deleted whenever the person disconnects or stops being remembered.
/// </summary>
public sealed class UbisoftSignInCredentialStore(string providerRoot, IUserCredentialProtector protector)
{
    private readonly string _file = Path.Combine(providerRoot, "signin.dat");

    public bool Exists => File.Exists(_file);

    public UbisoftSignInCredential? Load()
    {
        try
        {
            if (!File.Exists(_file)) return null;
            var json = protector.Unprotect(File.ReadAllText(_file));
            var credential = JsonSerializer.Deserialize<UbisoftSignInCredential>(json);
            return string.IsNullOrWhiteSpace(credential?.Email) || string.IsNullOrEmpty(credential.Password)
                ? null : credential;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException or JsonException
            or FormatException or ArgumentException)
        {
            // A profile that cannot be read is treated as "not remembered" instead of failing sign-in.
            return null;
        }
    }

    public void Save(UbisoftSignInCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential.Email);
        ArgumentException.ThrowIfNullOrEmpty(credential.Password);
        Directory.CreateDirectory(providerRoot);
        var temporary = _file + ".tmp";
        try
        {
            File.WriteAllText(temporary, protector.Protect(JsonSerializer.Serialize(credential)));
            File.Move(temporary, _file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Delete()
    {
        try { if (File.Exists(_file)) File.Delete(_file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
