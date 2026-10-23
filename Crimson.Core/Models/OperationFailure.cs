using Crimson.Resources;
namespace Crimson.Models;

public enum OperationFailureKind
{
    Network,
    Storage,
    Permission,
    Verification,
    Manifest,
    Unknown
}

public sealed record OperationFailure(
    OperationFailureKind Kind,
    string Code,
    string Message,
    string SuggestedAction,
    bool CanRetry)
{
    public static OperationFailure Verification(int invalidFileCount) => new(
        OperationFailureKind.Verification,
        "verification_failed",
        invalidFileCount == 1
            ? UiStrings.OneFileFailedVerification
            : UiStrings.Format(UiStrings.VerificationFailedSentenceFormat, invalidFileCount),
        UiStrings.RetryTheRepairIfItFailsAgainReportTheProblem,
        true);
}
