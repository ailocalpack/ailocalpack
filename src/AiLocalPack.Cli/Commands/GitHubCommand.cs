using System.CommandLine;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiLocalPack.Core.GitHub;
using AiLocalPack.Core.Launch;
using AiLocalPack.Core.Secrets;
using AiLocalPack.Core.Storage;

namespace AiLocalPack.Cli.Commands;

/// <summary>
/// <c>github</c> (task 58). <c>github login [--no-browser]</c>: GitHub Device Flow; prints the address and the code,
/// opens the browser (unless <c>--no-browser</c>), waits for the approval and keeps the token in the OS keychain (exit 0
/// when signed in; already signed in is a no-op). <c>github whoami [--json]</c>: the signed-in account, checked with
/// GitHub (exit 0 signed in, also when GitHub can't be reached: the remembered account with a warning; 1 signed out,
/// revoked or a keychain problem). <c>github logout</c>: deletes the token and the account here and prints where to
/// revoke the app's access on GitHub (the app has no client secret to revoke it itself).
/// </summary>
internal static class GitHubCommand
{
    public static Command Build(OsEnvironment env)
    {
        return new Command("github", "Sign in to GitHub (for publishing packs) and see who is signed in.")
        {
            BuildLogin(env),
            BuildWhoAmI(env),
            BuildLogout(env),
        };
    }

    private static Command BuildLogin(OsEnvironment env)
    {
        var noBrowser = new Option<bool>("--no-browser") { Description = "Don't open the browser; only print the address and the code." };
        var login = new Command("login", "Sign in to GitHub with a one-time code (Device Flow); the token goes into the OS keychain.") { noBrowser };
        login.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            using var http = CreateHttpClient();
            var session = CreateSession(env, http);
            if (session.Account is { } current && (await session.GetTokenAsync(cancellationToken)).IsOk)
            {
                output.WriteLine($"Already signed in to GitHub as {current.Login}; `ailocalpack github logout` first to switch accounts.");
                return 0;
            }

            var started = await session.StartSignInAsync(cancellationToken);
            if (started is DeviceCodeResult.Failed failed)
            {
                error.WriteLine(Problem(failed.Problem, failed.Detail, session.App));
                return 1;
            }

            var authorization = ((DeviceCodeResult.Started)started).Authorization;
            var minutes = Math.Max(1, (int)Math.Round((authorization.ExpiresAt - DateTimeOffset.UtcNow).TotalMinutes));
            output.WriteLine($"Open {authorization.VerificationUri} and enter the code: {authorization.UserCode}");
            output.WriteLine($"(the code works for {minutes} min; the app asks for: {GitHubOAuthApp.Scopes})");
            if (!result.GetValue(noBrowser))
            {
                try
                {
                    new SystemProcessStarter().OpenUrl(authorization.VerificationUri);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
                {
                    output.WriteLine($"(couldn't open the browser: {e.Message}; open the address yourself)");
                }
            }

            output.WriteLine("Waiting for you to approve it on GitHub...");
            switch (await session.FinishSignInAsync(authorization, cancellationToken))
            {
                case GitHubSignInResult.SignedIn signedIn:
                    output.WriteLine($"Signed in to GitHub as {Describe(signedIn.Account.Login, signedIn.Account.Name)}; the token is in {KeychainName(session.KeychainKind)}.");
                    return 0;
                case GitHubSignInResult.Expired:
                    error.WriteLine("The code expired before it was approved; run `ailocalpack github login` again.");
                    return 1;
                case GitHubSignInResult.Denied:
                    error.WriteLine("The sign-in was cancelled on GitHub.");
                    return 1;
                case GitHubSignInResult.NotSaved notSaved:
                    error.WriteLine($"GitHub approved {notSaved.User.Login}, but {KeychainName(session.KeychainKind)} couldn't keep the token, so you stay signed out (it is never saved in plain text): {notSaved.Keychain.Problem}");
                    return 1;
                case GitHubSignInResult.Failed signInFailed:
                    error.WriteLine(Problem(signInFailed.Problem, signInFailed.Detail, session.App));
                    return 1;
                default:
                    return 1;
            }
        });
        return login;
    }

    private static Command BuildWhoAmI(OsEnvironment env)
    {
        var json = new Option<bool>("--json") { Description = "Print the result as JSON." };
        var whoami = new Command("whoami", "Print the GitHub account the app is signed in as (checked with GitHub).") { json };
        whoami.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            using var http = CreateHttpClient();
            var session = CreateSession(env, http);
            var check = await session.CheckAsync(cancellationToken);
            if (result.GetValue(json))
            {
                output.WriteLine(WhoAmIJson(check, session.KeychainKind));
                return check is GitHubAccountCheck.SignedIn or GitHubAccountCheck.Unverified { Account: not null } ? 0 : 1;
            }

            switch (check)
            {
                case GitHubAccountCheck.SignedIn signedIn:
                    WriteAccount(output, signedIn.Account, session.KeychainKind);
                    return 0;
                case GitHubAccountCheck.Unverified { Account: { } remembered } unverified:
                    WriteAccount(output, remembered, session.KeychainKind);
                    error.WriteLine($"warning: not checked with GitHub ({unverified.Detail})");
                    return 0;
                case GitHubAccountCheck.Unverified unverified:
                    error.WriteLine($"A GitHub token is kept, but GitHub can't be asked whose it is: {unverified.Detail}");
                    return 1;
                case GitHubAccountCheck.Revoked revoked:
                    error.WriteLine($"GitHub no longer accepts the token of {revoked.Login} (access revoked); signed out. `ailocalpack github login` signs in again.");
                    return 1;
                case GitHubAccountCheck.KeychainProblem problem:
                    error.WriteLine($"Can't read the GitHub token from {KeychainName(session.KeychainKind)}: {problem.Keychain.Problem}");
                    return 1;
                default:
                    error.WriteLine("Not signed in to GitHub; `ailocalpack github login` signs in.");
                    return 1;
            }
        });
        return whoami;
    }

    private static Command BuildLogout(OsEnvironment env)
    {
        var logout = new Command("logout", "Sign out of GitHub: delete the token from the OS keychain.");
        logout.SetAction(async (result, cancellationToken) =>
        {
            var output = result.InvocationConfiguration.Output;
            var error = result.InvocationConfiguration.Error;
            using var http = CreateHttpClient();
            var session = CreateSession(env, http);
            var login = session.Account?.Login;
            var deleted = await session.SignOutAsync(cancellationToken);
            if (!deleted.IsOk)
            {
                error.WriteLine($"{KeychainName(session.KeychainKind)} couldn't delete the GitHub token: {deleted.Problem}");
                return 1;
            }

            output.WriteLine(login is null ? "Signed out of GitHub (no token was kept)." : $"Signed out of GitHub ({login}); the token is deleted from {KeychainName(session.KeychainKind)}.");
            output.WriteLine($"To revoke the app's access on GitHub too: {session.App.ConnectionsPage}");
            return 0;
        });
        return logout;
    }

    private static void WriteAccount(TextWriter output, GitHubAccount account, SecretStoreKind keychain)
    {
        output.WriteLine(Describe(account.Login, account.Name));
        output.WriteLine($"profile:  {account.ProfileUrl}");
        output.WriteLine($"scopes:   {(account.Scopes.Count == 0 ? "(none reported)" : string.Join(", ", account.Scopes))}");
        output.WriteLine($"token:    in {KeychainName(keychain)}");
    }

    private static string WhoAmIJson(GitHubAccountCheck check, SecretStoreKind keychain)
    {
        var (account, state, problem) = check switch
        {
            GitHubAccountCheck.SignedIn s => (s.Account, "signedIn", null),
            GitHubAccountCheck.Unverified u => (u.Account, "unverified", u.Detail),
            GitHubAccountCheck.Revoked r => ((GitHubAccount?)null, "revoked", $"GitHub no longer accepts the token of {r.Login}."),
            GitHubAccountCheck.KeychainProblem k => (k.Account, "keychainProblem", k.Keychain.Problem),
            _ => ((GitHubAccount?)null, "signedOut", (string?)null),
        };
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("state", state);
            json.WriteString("login", account?.Login);
            json.WriteString("name", account?.Name);
            json.WriteString("avatarUrl", account?.AvatarUrl?.AbsoluteUri);
            json.WriteString("profileUrl", account?.ProfileUrl.AbsoluteUri);
            json.WriteStartArray("scopes");
            foreach (var scope in account?.Scopes ?? [])
            {
                json.WriteStringValue(scope);
            }

            json.WriteEndArray();
            json.WriteString("keychain", JsonNamingPolicy.CamelCase.ConvertName(keychain.ToString()));
            json.WriteString("problem", problem);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Describe(string login, string? name) => name is null ? login : $"{login} ({name})";

    private static string KeychainName(SecretStoreKind kind) => kind switch
    {
        SecretStoreKind.WindowsCredentialManager => "Windows Credential Manager",
        SecretStoreKind.MacKeychain => "the macOS Keychain",
        SecretStoreKind.SecretService => "the keyring (Secret Service)",
        SecretStoreKind.File => $"the dev secrets file ({SecretStores.FileVariable})",
        _ => "memory",
    };

    private static string Problem(GitHubAuthProblem problem, string detail, GitHubOAuthApp app) => problem switch
    {
        GitHubAuthProblem.Network => $"Can't reach GitHub: {detail}",
        GitHubAuthProblem.DeviceFlowDisabled => $"The GitHub app {app.ClientId} has Device Flow switched off: {detail}",
        GitHubAuthProblem.UnknownClient => $"GitHub doesn't know the app's client ID {app.ClientId} ({GitHubOAuthApp.ClientIdVariable} sets another): {detail}",
        _ => detail,
    };

    private static HttpClient CreateHttpClient() =>
        GitHubOAuthApp.CreateHttpClient(new ProductInfoHeaderValue("ailocalpack-cli", typeof(GitHubCommand).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"));

    private static GitHubSession CreateSession(OsEnvironment env, HttpClient http)
    {
        var app = GitHubOAuthApp.Resolve(env);
        return new GitHubSession(
            SecretStores.Create(env),
            new GitHubDeviceFlow(http, app),
            new GitHubUserClient(http, app),
            AppPaths.Resolve(env),
            app);
    }
}
