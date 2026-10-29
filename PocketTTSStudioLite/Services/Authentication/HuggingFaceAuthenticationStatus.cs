namespace PocketTTSStudioLite.Services.Authentication;

public enum HuggingFaceAuthenticationState
{
    SignedOut,
    RequestingCode,
    WaitingForAuthorization,
    SignedIn,
    Expired,
    Rejected,
    Cancelled,
    Failed,
}

/// <summary>UI-safe status. Contains no device code, access token, ID token, or refresh token.</summary>
public sealed record HuggingFaceAuthenticationStatus(
    HuggingFaceAuthenticationState State,
    string Message,
    string? UserCode = null,
    Uri? VerificationUri = null,
    DateTimeOffset? DeviceCodeExpiresAtUtc = null,
    DateTimeOffset? SessionExpiresAtUtc = null);
