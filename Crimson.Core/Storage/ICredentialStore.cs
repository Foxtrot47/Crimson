using Crimson.Models;

namespace Crimson.Core;

public interface ICredentialStore
{
    Task<UserData?> GetUserData();

    Task SaveUserData(UserData? data);

    Task ClearUserData();
}
