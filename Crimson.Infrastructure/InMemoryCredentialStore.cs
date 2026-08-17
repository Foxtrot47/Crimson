using Crimson.Core;
using Crimson.Models;

namespace Crimson.Infrastructure;

public sealed class InMemoryCredentialStore : ICredentialStore
{
    private UserData? _userData;

    public Task<UserData?> GetUserData() => Task.FromResult(_userData);

    public Task SaveUserData(UserData? data)
    {
        _userData = data;
        return Task.CompletedTask;
    }

    public Task ClearUserData()
    {
        _userData = null;
        return Task.CompletedTask;
    }
}
