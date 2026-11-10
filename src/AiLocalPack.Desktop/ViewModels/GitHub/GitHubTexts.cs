using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Desktop.Localization;
using AiLocalPack.Desktop.Resources;

namespace AiLocalPack.Desktop.ViewModels.GitHub;

/// <summary>Words for GitHub sign-in shared by Settings → GitHub and the Device Flow panel.</summary>
internal static class GitHubTexts
{
    /// <summary>Where the token is kept ("macOS Keychain").</summary>
    public static string KeychainName(Localizer localizer, SecretStoreKind kind) => localizer[kind switch
    {
        SecretStoreKind.WindowsCredentialManager => nameof(Strings.GitHub_Keychain_Windows),
        SecretStoreKind.MacKeychain => nameof(Strings.GitHub_Keychain_Mac),
        SecretStoreKind.SecretService => nameof(Strings.GitHub_Keychain_SecretService),
        SecretStoreKind.File => nameof(Strings.GitHub_Keychain_File),
        _ => nameof(Strings.GitHub_Keychain_Memory),
    }];

    /// <summary>Why asking GitHub for a code or a token failed.</summary>
    public static string Problem(Localizer localizer, GitHubAuthProblem problem, string detail) => problem switch
    {
        GitHubAuthProblem.Network => localizer[nameof(Strings.GitHubSignIn_Error_Network)],
        _ => localizer.Format(nameof(Strings.GitHubSignIn_Error_Refused), detail),
    };

    /// <summary>"github.com/login/device": the address without the scheme, as the player types it.</summary>
    public static string ShortAddress(Uri uri) => (uri.Authority + uri.AbsolutePath).TrimEnd('/');
}
