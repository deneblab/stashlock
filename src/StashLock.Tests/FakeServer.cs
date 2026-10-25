using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Deneblab.StashLock.Tests;

/// <summary>Minimal HTTP server for tests: the handler maps the request path to (status, body).</summary>
internal sealed class FakeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public string Url { get; }

    public FakeServer(Func<string, (int Status, string Body)> respond)
    {
        var port = FreePort();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                var (status, body) = respond(ctx.Request.Url!.AbsolutePath);
                ctx.Response.StatusCode = status;
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
    }

    public FakeServer(Func<string, int> status)
        : this(path =>
        {
            var code = status(path);
            return (code, code == 200 ? "{\"status\":\"ok\",\"key\":\"x\"}" : "{\"status\":" + code + ",\"detail\":\"denied\"}");
        })
    {
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose() => _listener.Close();
}
