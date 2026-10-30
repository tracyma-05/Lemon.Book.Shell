using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Lemon.Book.Shell.Infrastructures.Localization;
using Lemon.Book.Shell.Sync;

namespace Lemon.Book.Shell.Desktop;

/// <summary>
/// Signing in on the desktop (RFC 8252): the sign-in page opens in the system browser, where the reader's saved
/// passwords and an existing sign-in work, and its answer comes back to a one-off listener on a fixed loopback port,
/// which is the redirect address registered for the client. A plain socket rather than HttpListener: on Windows that
/// needs a URL reservation for 127.0.0.1, and only one request line is read here.
/// </summary>
public sealed class LoopbackSignInFlow : ISignInFlow
{
    /// <summary>Registered with the sign-in service as http://127.0.0.1:47613/signin.</summary>
    internal const int Port = 47613;

    private const string CallbackPath = "/signin";

    public Uri RedirectUri { get; } = new($"http://127.0.0.1:{Port}{CallbackPath}");

    public async Task<string?> SignInAsync(Uri authorizeUrl, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            // Another copy of the book is signing in (or something else holds the port).
            throw new SignInException($"Port {Port} is busy: {ex.SocketErrorCode}");
        }

        try
        {
            OpenBrowser(authorizeUrl);
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

    /// <summary>Reads one request; the callback's query, or null for anything else (a favicon).</summary>
    private static async Task<string?> AnswerAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;

        // "GET /signin?code=…&state=… HTTP/1.1"
        var parts = requestLine.Split(' ');
        var target = parts.Length >= 2 ? parts[1] : string.Empty;
        var at = target.IndexOf('?');
        var query = at >= 0 ? target[at..] : string.Empty;
        var isCallback = parts[0] == "GET" && (at < 0 ? target : target[..at]) == CallbackPath;

        var strings = LocalizationService.Instance;
        var failed = query.Contains("error=", StringComparison.Ordinal);
        var message = WebUtility.HtmlEncode(strings.GetString(failed ? "Account_Callback_Failed" : "Account_Callback_Done"));
        var html = isCallback
            ? "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>柠檬绘本</title>"
              + "<style>body{font-family:system-ui,sans-serif;background:#fbf5e8;color:#3a2a1e;display:grid;place-items:center;"
              + "min-height:100vh;margin:0}p{font-size:1.4rem;padding:24px}</style></head>"
              + $"<body><p>{message}</p></body></html>"
            : string.Empty;
        var body = Encoding.UTF8.GetBytes(html);
        var head = isCallback
            ? $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n"
            : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        return isCallback ? query : null;
    }

    private static void OpenBrowser(Uri url)
    {
        var start = OperatingSystem.IsMacOS()
            ? new ProcessStartInfo("open") { ArgumentList = { url.ToString() } }
            : OperatingSystem.IsWindows()
                ? new ProcessStartInfo(url.ToString()) { UseShellExecute = true }
                : new ProcessStartInfo("xdg-open") { ArgumentList = { url.ToString() } };
        using var process = Process.Start(start);
    }
}
