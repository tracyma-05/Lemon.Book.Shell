using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Lemon.Hub.Client.Desktop;

/// <summary>
/// Signing in on the desktop (RFC 8252): the sign-in page opens in the system browser, where the user's saved
/// passwords and an existing sign-in work, and its answer comes back to a one-off listener on a fixed loopback port,
/// which is the redirect address registered for the client. A plain socket rather than HttpListener: on Windows that
/// needs a URL reservation for 127.0.0.1, and only one request line is read here.
/// </summary>
public sealed class LoopbackSignInFlow : ISignInFlow
{
    /// <summary>The path of the redirect address: <c>http://127.0.0.1:&lt;port&gt;/signin</c>.</summary>
    public const string CallbackPath = "/signin";

    private readonly Func<bool, string> _page;
    private readonly Action<Uri> _openBrowser;

    /// <param name="port">
    /// The app's own port: its client is registered with <c>http://127.0.0.1:&lt;port&gt;/signin</c>, and apps on
    /// different ports can sign in at the same time.
    /// </param>
    /// <param name="callbackPage">
    /// The page the browser shows once the answer arrived, as a whole HTML document; its argument says whether the
    /// sign-in failed (the service answered with an error). <see cref="Page"/> makes a plain one.
    /// </param>
    /// <param name="openBrowser">Shows the sign-in page; the system browser unless the app has its own way.</param>
    public LoopbackSignInFlow(int port, Func<bool, string>? callbackPage = null, Action<Uri>? openBrowser = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        Port = port;
        RedirectUri = new Uri($"http://127.0.0.1:{port}{CallbackPath}");
        _page = callbackPage ?? DefaultPage;
        _openBrowser = openBrowser ?? OpenSystemBrowser;
    }

    public int Port { get; }

    public Uri RedirectUri { get; }

    public async Task<string?> SignInAsync(Uri authorizeUrl, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            // Another copy of the app is signing in (or something else holds the port).
            throw new SignInException($"Port {Port} is busy: {ex.SocketErrorCode}");
        }

        try
        {
            _openBrowser(authorizeUrl);
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken);
                var answer = await AnswerAsync(client, cancellationToken);
                if (answer is not null) return answer;
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>A small page saying <paramref name="message"/>, titled <paramref name="title"/> (both HTML-encoded here).</summary>
    public static string Page(string title, string message, string language = "en") =>
        $"<!DOCTYPE html><html lang=\"{WebUtility.HtmlEncode(language)}\"><head><meta charset=\"utf-8\">"
        + $"<title>{WebUtility.HtmlEncode(title)}</title>"
        + "<style>body{font-family:system-ui,sans-serif;display:grid;place-items:center;min-height:100vh;margin:0}"
        + "p{font-size:1.4rem;padding:24px}</style></head>"
        + $"<body><p>{WebUtility.HtmlEncode(message)}</p></body></html>";

    private static string DefaultPage(bool failed) => failed
        ? Page("Sign-in", "Sign-in did not finish. Go back to the app to try again.")
        : Page("Sign-in", "You are signed in. You can close this page and go back to the app.");

    /// <summary>Reads one request; the callback's query, or null for anything else (a favicon).</summary>
    private async Task<string?> AnswerAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;

        // "GET /signin?code=…&state=… HTTP/1.1"
        var (isCallback, query) = ReadRequestLine(requestLine);
        var failed = query.Contains("error=", StringComparison.Ordinal);
        var body = isCallback ? Encoding.UTF8.GetBytes(_page(failed)) : [];
        var head = isCallback
            ? $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n"
            : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        return isCallback ? query : null;
    }

    /// <summary>Whether a request line is the callback (<c>GET /signin…</c>), and its query (with the <c>?</c>).</summary>
    internal static (bool IsCallback, string Query) ReadRequestLine(string requestLine)
    {
        var parts = requestLine.Split(' ');
        var target = parts.Length >= 2 ? parts[1] : string.Empty;
        var at = target.IndexOf('?');
        var query = at >= 0 ? target[at..] : string.Empty;
        var isCallback = parts[0] == "GET" && (at < 0 ? target : target[..at]) == CallbackPath;
        return (isCallback, query);
    }

    private static void OpenSystemBrowser(Uri url)
    {
        var start = OperatingSystem.IsMacOS()
            ? new ProcessStartInfo("open") { ArgumentList = { url.ToString() } }
            : OperatingSystem.IsWindows()
                ? new ProcessStartInfo(url.ToString()) { UseShellExecute = true }
                : new ProcessStartInfo("xdg-open") { ArgumentList = { url.ToString() } };
        using var process = Process.Start(start);
    }
}
