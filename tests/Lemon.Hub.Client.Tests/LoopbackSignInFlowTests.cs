using System.Net;
using System.Net.Sockets;
using Lemon.Hub.Client.Desktop;
using Xunit;

namespace Lemon.Hub.Client.Tests;

public sealed class LoopbackSignInFlowTests
{
    [Fact]
    public async Task HearsTheAnswerOnItsPortAndShowsTheAppsPage()
    {
        var port = FreePort();
        using var http = new HttpClient();
        Task<HttpResponseMessage>? favicon = null;
        Task<HttpResponseMessage>? callback = null;
        var flow = new LoopbackSignInFlow(
            port,
            failed => LoopbackSignInFlow.Page("App", failed ? "Try again" : "Signed in <ok>"),
            // The "browser": asks for a favicon first, then comes back with the answer.
            openBrowser: _ =>
            {
                favicon = http.GetAsync(new Uri($"http://127.0.0.1:{port}/favicon.ico"));
                callback = favicon.ContinueWith(_ => http.GetAsync(new Uri($"http://127.0.0.1:{port}/signin?code=c1&state=s1")), TaskScheduler.Default).Unwrap();
            });

        Assert.Equal(new Uri($"http://127.0.0.1:{port}/signin"), flow.RedirectUri);

        var answer = await flow.SignInAsync(new Uri("https://sso.example.test/connect/authorize"), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal("?code=c1&state=s1", answer);
        Assert.Equal(HttpStatusCode.NotFound, (await favicon!).StatusCode);
        var page = await (await callback!).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Signed in &lt;ok&gt;", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABusyPortIsASignInFailure()
    {
        var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        try
        {
            var port = ((IPEndPoint)holder.LocalEndpoint).Port;
            var flow = new LoopbackSignInFlow(port, openBrowser: _ => { });
            await Assert.ThrowsAsync<SignInException>(() => flow.SignInAsync(new Uri("https://sso.example.test/"), TestContext.Current.CancellationToken));
        }
        finally
        {
            holder.Stop();
        }
    }

    [Theory]
    [InlineData("GET /signin?code=a&state=b HTTP/1.1", true, "?code=a&state=b")]
    [InlineData("GET /signin HTTP/1.1", true, "")]
    [InlineData("GET /favicon.ico HTTP/1.1", false, "")]
    [InlineData("POST /signin?code=a HTTP/1.1", false, "?code=a")]
    [InlineData("", false, "")]
    public void ReadsTheRequestLine(string line, bool isCallback, string query)
    {
        Assert.Equal((isCallback, query), LoopbackSignInFlow.ReadRequestLine(line));
    }

    [Fact]
    public void RefusesAPortOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopbackSignInFlow(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopbackSignInFlow(70000));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
