namespace Crimson.Repository;

public interface IAccessTokenProvider
{
    Task<string?> GetAccessToken(CancellationToken cancellationToken = default);
}
