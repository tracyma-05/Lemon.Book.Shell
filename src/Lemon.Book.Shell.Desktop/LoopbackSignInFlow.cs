using System.Net;
using Lemon.Book.Shell.Infrastructures.Localization;
using Lemon.Hub.Client;

namespace Lemon.Book.Shell.Desktop;

/// <summary>
/// Signing in on the desktop, as the books do: <see cref="Lemon.Hub.Client.Desktop.LoopbackSignInFlow"/> (the system
/// browser, the answer on a loopback port) with the books' page and words once the answer arrived.
/// </summary>
/// <param name="port">
/// The book's own port: each book's client is registered with http://127.0.0.1:&lt;port&gt;/signin, and books on
/// different ports can sign in at the same time.
/// </param>
public sealed class LoopbackSignInFlow(int port = LoopbackSignInFlow.DefaultPort) : ISignInFlow
{
    /// <summary>The first book's port (http://127.0.0.1:47613/signin).</summary>
    public const int DefaultPort = 47613;

    private readonly Lemon.Hub.Client.Desktop.LoopbackSignInFlow _flow = new(port, BookPage);

    public int Port => _flow.Port;

    public Uri RedirectUri => _flow.RedirectUri;

    public Task<string?> SignInAsync(Uri authorizeUrl, CancellationToken cancellationToken) =>
        _flow.SignInAsync(authorizeUrl, cancellationToken);

    private static string BookPage(bool failed)
    {
        var message = WebUtility.HtmlEncode(LocalizationService.Instance.GetString(failed ? "Account_Callback_Failed" : "Account_Callback_Done"));
        return "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>柠檬绘本</title>"
               + "<style>body{font-family:system-ui,sans-serif;background:#fbf5e8;color:#3a2a1e;display:grid;place-items:center;"
               + "min-height:100vh;margin:0}p{font-size:1.4rem;padding:24px}</style></head>"
               + $"<body><p>{message}</p></body></html>";
    }
}
