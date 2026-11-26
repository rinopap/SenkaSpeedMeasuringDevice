using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LocalCdpTestServer;

internal static class Program
{
    private const string ApiBody = "{\"api_data\":{\"api_basic\":{\"api_experience\":123456789},\"api_ship\":[{\"api_exp\":[500,0]}]}}";

    private static async Task Main(string[] args)
    {
        var port = int.Parse(args[0]);
        var fetchDelaySeconds = args.Length > 1 ? Math.Clamp(int.Parse(args[1]), 1, 300) : 10;
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"127.0.0.1:{port}");
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleAsync(client, fetchDelaySeconds, port);
        }
    }

    private static async Task HandleAsync(TcpClient client, int fetchDelaySeconds, int port)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync() ?? string.Empty;
            string? line;
            do { line = await reader.ReadLineAsync(); } while (!string.IsNullOrEmpty(line));
            var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
            var (body, contentType) = path switch
            {
                "/" => ($"<!doctype html><meta charset=\"utf-8\"><script>setTimeout(()=>fetch('/kcsapi/api_port/port').then(r=>r.json()).then(x=>document.title='done'),{fetchDelaySeconds * 1000})</script>", "text/html; charset=utf-8"),
                "/iframe" => ($"<!doctype html><meta charset=\"utf-8\"><iframe src=\"http://localhost:{port}/frame\"></iframe>", "text/html; charset=utf-8"),
                "/frame" => ($"<!doctype html><meta charset=\"utf-8\"><script>setTimeout(()=>fetch('/kcsapi/api_port/port').then(r=>r.json()).then(x=>document.title='done'),{fetchDelaySeconds * 1000})</script>", "text/html; charset=utf-8"),
                "/kcsapi/api_port/port" => (ApiBody, "application/json; charset=utf-8"),
                _ => ("", "text/plain")
            };
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(bodyBytes);
            await stream.FlushAsync();
        }
    }
}

