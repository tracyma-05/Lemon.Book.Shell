using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Lemon.Book.Shell.Sync;

namespace Lemon.Book.Shell.Browser;

/// <summary>
/// Signing in on the web: the page goes to the sign-in service, which sends the browser back to <c>signin.html</c>
/// beside it. That page leaves the answer in <c>localStorage</c> and opens the book again, which finishes the sign-in
/// on start (<see cref="SyncService.StartAsync"/>). What the answer must match was saved before leaving.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class RedirectSignInFlow : ISignInFlow
{
    public Uri RedirectUri { get; } = new(new Uri(PageAddress()), "signin.html");

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
