using System;
using System.Net;

namespace Crimson.Repository;

public enum RepositoryFailureKind
{
    Authentication,
    Http,
    Network,
    InvalidResponse,
    Policy,
    SizeLimit
}

public sealed record RepositoryFailure(
    RepositoryFailureKind Kind,
    string Message,
    HttpStatusCode? StatusCode = null);

public sealed class RepositoryResult<T>
{
    private readonly T? _value;

    private RepositoryResult(T value)
    {
        _value = value;
    }

    private RepositoryResult(RepositoryFailure failure)
    {
        Failure = failure;
    }

    public bool IsSuccess => Failure is null;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed repository result has no value.");

    public RepositoryFailure? Failure { get; }

    public static RepositoryResult<T> Success(T value) => new(value);

    public static RepositoryResult<T> Failed(RepositoryFailure failure) => new(failure);
}

public enum EpicPayloadPlatform
{
    Windows
}

internal static class EpicPayloadPlatformExtensions
{
    public static string ToApiValue(this EpicPayloadPlatform platform) => platform switch
    {
        EpicPayloadPlatform.Windows => "Windows",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
    };
}
