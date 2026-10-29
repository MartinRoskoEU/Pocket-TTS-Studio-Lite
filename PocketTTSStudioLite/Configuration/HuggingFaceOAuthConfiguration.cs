namespace PocketTTSStudioLite.Configuration;

/// <summary>Public application registration. No client secret is used or supported.</summary>
public static class HuggingFaceOAuthConfiguration
{
    public const string ClientId = "e5f65004-0823-403e-8c4a-1fd4f23db279";
    public const string Scopes = "openid gated-repos";
    public static Uri DeviceEndpoint { get; } = new("https://huggingface.co/oauth/device");
    public static Uri TokenEndpoint { get; } = new("https://huggingface.co/oauth/token");
}
