namespace Lemon.Hub.Client;

/// <summary>
/// How this head shows the sign-in page and hears its answer. The desktop opens the system browser and listens on a
/// loopback address (Lemon.Hub.Client.Desktop); the web edition goes to the page itself and is started again at its
/// sign-in callback (Lemon.Hub.Client.Browser).
/// </summary>
public interface ISignInFlow
{
    /// <summary>Where the sign-in service sends its answer; registered with the service for this client.</summary>
    Uri RedirectUri { get; }

    /// <summary>
    /// Shows <paramref name="authorizeUrl"/> and returns the answer's query string (<c>?code=…&amp;state=…</c>), or null
    /// when the answer comes later, to a new start of the app (the web edition: see <see cref="HubAccount.CompleteAsync"/>).
    /// </summary>
    Task<string?> SignInAsync(Uri authorizeUrl, CancellationToken cancellationToken);
}
