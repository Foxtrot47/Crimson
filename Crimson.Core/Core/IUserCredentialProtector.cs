namespace Crimson.Core;

/// <summary>
/// Protects secrets that must stay readable only to the signed-in Windows user. A stored sign-in
/// password belongs here rather than beside long-lived tokens, which use the machine-wide protector.
/// </summary>
public interface IUserCredentialProtector : ICredentialProtector;
