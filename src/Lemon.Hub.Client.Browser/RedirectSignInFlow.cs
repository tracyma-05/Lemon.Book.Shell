using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Lemon.Hub.Client.Browser;

/// <summary>
/// Signing in on the web: the page goes to the sign-in service, which sends the browser back to the app's callback
/// page beside it (<c>signin.html</c>). That page leaves the answer where the app reads it on start, and opens the app
/// again, which finishes the sign-in (<see cref="HubAccount.CompleteAsync"/>). What the answer must match was saved
/// before leaving, so the app's <see cref="ISessionStore"/> must outlive the page (<c>localStorage</c>).
/// </summary>
/// <param name="callbackPage">The callback page, relative to the app's own address; registered with the client.</param>
[SupportedOSPlatform("browser")]
public sealed partial class RedirectSignInFlow(string callbackPage = RedirectSignInFlow.DefaultCallbackPage) : ISignInFlow
{
    public const string DefaultCallbackPage = "signin.html";

    public Uri RedirectUri { get; } = new(new Uri(PageAddress()), callbackPage);

    public Task<string?> SignInAsync(Uri authorizeUrl, CancellationToken cancellationToken)
    {
        Navigate(authorizeUrl.ToString());
        return Task.FromResult<string?>(null);
    }

    /// <summary>The page's own address (its <c>base</c>), without a query.</summary>
    private static string PageAddress()
    {
        using var document = JSHost.GlobalThis.GetPropertyAsJSObject("document");
        var address = document?.GetPropertyAsString("baseURI") ?? "/";
        var end = address.IndexOfAny(['?', '#']);
        return end < 0 ? address : address[..end];
    }

    [JSImport("globalThis.location.assign")]
    private static partial void Navigate(string url);
}
