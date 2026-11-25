using System.Text;
using PlaxisProMax.Mcp;

/// <summary>HTTP requests read off the wire, as Sketch ProMax's ChatMcpTests check them (the server is the same code).</summary>
internal static class McpHttpTests
{
    private static HttpRequest? Read(string raw) =>
        HttpRequest.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(raw)), CancellationToken.None).GetAwaiter().GetResult();

    private static int Refused(string raw)
    {
        try { Read(raw); return 0; }
        catch (HttpRequest.Refused r) { return r.Status; }
    }

    public static void Run(Action<string, bool, string> check)
    {
        var post = Read("POST /mcp?x=1 HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: 13\r\nAccept: a\r\naccept: b\r\n\r\n{\"jsonrpc\":1}");
        check("mcp http: a POST with Content-Length", post is { Method: "POST", Path: "/mcp" } && Encoding.UTF8.GetString(post.Body) == "{\"jsonrpc\":1}"
            && post.Header("content-type") == "application/json" && post.Header("Accept") == "a, b", post?.Path ?? "null");
        var chunked = Read("POST /mcp HTTP/1.1\nTransfer-Encoding: chunked\n\n4\r\n{\"a\"\r\n3;ext=1\r\n:1}\r\n0\r\n\r\n");
        check("mcp http: a chunked body, and lines ending in LF alone", chunked is not null && Encoding.UTF8.GetString(chunked.Body) == "{\"a\":1}",
            chunked is null ? "null" : Encoding.UTF8.GetString(chunked.Body));
        check("mcp http: a closed connection is no request", Read("") is null, "");
        check("mcp http: bad requests are refused with their status",
            Refused("GARBAGE\r\n\r\n") == 400 && Refused("POST /mcp HTTP/1.1\r\nContent-Length: 10\r\n\r\n{}") == 400
            && Refused("POST /mcp HTTP/1.1\r\nContent-Length: 999999999\r\n\r\n") == 413
            && Refused("GET / HTTP/1.1\r\nX: " + new string('a', HttpRequest.MaxHead + 10) + "\r\n\r\n") == 431,
            $"{Refused("GARBAGE\r\n\r\n")} {Refused("POST /mcp HTTP/1.1\r\nContent-Length: 999999999\r\n\r\n")}");
        var answer = Encoding.ASCII.GetString(HttpRequest.Response(202, [], null));
        check("mcp http: every answer closes the connection", answer.StartsWith("HTTP/1.1 202 Accepted\r\n") && answer.Contains("Content-Length: 0\r\n")
            && answer.EndsWith("Connection: close\r\n\r\n"), answer);
    }
}
