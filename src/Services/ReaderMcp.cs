using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Maison.Services;

/// <summary>
/// maison's built-in "reader": a tiny MCP server (maison.exe --mcp-reader, newline-delimited JSON-RPC on stdin/stdout)
/// that fetches a web page or online file and hands the agent clean text, in pages, so a small context window isn't
/// flooded. It needs no Node and no browser; it is the fallback when the full browser is off or can't be started.
/// </summary>
public static class ReaderMcp
{
    public const string Arg = "--mcp-reader";
    private const int DefaultChunk = 8000;
    private const int MaxChunk = 30000;

    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        h.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain,application/json,*/*;q=0.8");
        h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en;q=0.9");
        return h;
    }

    /// <summary>Serves requests until stdin closes.</summary>
    public static async Task RunAsync()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = Console.OpenStandardOutput();
        string? line;
        while ((line = await input.ReadLineAsync()) is not null)
        {
            if (line.Trim().Length == 0) continue;
            JsonNode? reply = null;
            try { reply = await HandleAsync(JsonNode.Parse(line)); }
            catch (Exception ex) { Console.Error.WriteLine("reader: " + ex.Message); }
            if (reply is null) continue;
            var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n");
            await output.WriteAsync(bytes);
            await output.FlushAsync();
        }
    }

    internal static async Task<JsonNode?> HandleAsync(JsonNode? msg)
    {
        if (msg is not JsonObject req) return null;
        var method = req["method"]?.GetValue<string>();
        var id = req["id"]?.DeepClone();
        if (id is null) return null;   // notification (e.g. notifications/initialized): no reply
        JsonObject Ok(JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        JsonObject Err(int code, string text) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = text } };

        switch (method)
        {
            case "initialize":
                return Ok(new JsonObject
                {
                    ["protocolVersion"] = req["params"]?["protocolVersion"]?.GetValue<string>() ?? "2024-11-05",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "maison-reader", ["version"] = "1.0" },
                });
            case "ping":
                return Ok(new JsonObject());
            case "tools/list":
                return Ok(new JsonObject { ["tools"] = new JsonArray(ToolDef()) });
            case "tools/call":
                var args = req["params"]?["arguments"] as JsonObject ?? new JsonObject();
                if (req["params"]?["name"]?.GetValue<string>() != "read_url") return Err(-32602, "Unknown tool");
                string text; bool bad = false;
                try { text = await ReadUrlAsync(args); }
                catch (Exception ex) { text = "Could not read that URL: " + ex.Message; bad = true; }
                return Ok(new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = bad,
                });
            default:
                return Err(-32601, "Method not found");
        }
    }

    private static JsonObject ToolDef() => new()
    {
        ["name"] = "read_url",
        ["description"] = "Fetch a web page or online text file (HTML, plain text, JSON, Markdown, source code, GitHub files) and return it as clean text. " +
                          "Long pages are returned in parts: pass 'start' (a character offset) to continue where the previous part ended. " +
                          "Does not run JavaScript; if the page comes back empty or incomplete, use the browser tools instead.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["url"] = new JsonObject { ["type"] = "string", ["description"] = "http(s) address to read" },
                ["start"] = new JsonObject { ["type"] = "integer", ["description"] = "Character offset to start from (default 0)" },
                ["max_chars"] = new JsonObject { ["type"] = "integer", ["description"] = $"Most characters to return (default {DefaultChunk}, max {MaxChunk})" },
            },
            ["required"] = new JsonArray("url"),
        },
    };

    private static async Task<string> ReadUrlAsync(JsonObject args)
    {
        var raw = args["url"]?.ToString()?.Trim() ?? "";
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("give a full http:// or https:// address");
        uri = RawGitHub(uri);
        int start = Math.Max(0, args["start"] is { } s ? (int)s.GetValue<double>() : 0);
        int max = Math.Clamp(args["max_chars"] is { } m ? (int)m.GetValue<double>() : DefaultChunk, 500, MaxChunk);

        using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"the site answered {(int)resp.StatusCode} {resp.ReasonPhrase}");
        if (type.StartsWith("image/") || type.StartsWith("audio/") || type.StartsWith("video/") || type is "application/zip" or "application/octet-stream" or "application/pdf")
            throw new InvalidOperationException($"that is a {type} file, which the reader can't turn into text");

        var bytes = await ReadCappedAsync(resp, 5_000_000);
        var body = Decode(bytes, resp.Content.Headers.ContentType?.CharSet);
        var text = type is "text/html" or "application/xhtml+xml" || (type == "" && body.TrimStart().StartsWith('<')) ? HtmlToText(body) : body;

        if (text.Trim().Length == 0) return $"{uri}\n(The page has no readable text; it probably builds itself with JavaScript. Use the browser tools.)";
        if (start >= text.Length) return $"{uri}\n(End of page: it is {text.Length} characters long.)";
        var part = text.Substring(start, Math.Min(max, text.Length - start));
        var end = start + part.Length;
        var head = $"{uri}  [characters {start}-{end} of {text.Length}]\n\n";
        return head + part + (end < text.Length ? $"\n\n[More: call read_url again with start={end}]" : "\n\n[End of page]");
    }

    private static Uri RawGitHub(Uri u)
    {
        if (u.Host != "github.com") return u;
        var m = Regex.Match(u.AbsolutePath, @"^/([^/]+)/([^/]+)/blob/(.+)$");
        return m.Success ? new Uri($"https://raw.githubusercontent.com/{m.Groups[1].Value}/{m.Groups[2].Value}/{m.Groups[3].Value}") : u;
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage resp, int cap)
    {
        await using var s = await resp.Content.ReadAsStreamAsync();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length >= cap) break;
        }
        return ms.ToArray();
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        try { if (!string.IsNullOrWhiteSpace(charset)) return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes); }
        catch { /* unknown charset: fall back to UTF-8 */ }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Strips a page down to its readable text, keeping headings, lists, links and code in a light Markdown form.</summary>
    internal static string HtmlToText(string html)
    {
        var h = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
        h = Regex.Replace(h, @"<(script|style|noscript|svg|head|template|iframe|nav|footer|form)\b.*?</\1\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        h = Regex.Replace(h, @"<h([1-6])\b[^>]*>", m => "\n\n" + new string('#', int.Parse(m.Groups[1].Value)) + " ", RegexOptions.IgnoreCase);
        h = Regex.Replace(h, @"<li\b[^>]*>", "\n- ", RegexOptions.IgnoreCase);
        h = Regex.Replace(h, @"<a\b[^>]*?href\s*=\s*[""']([^""'#][^""']*)[""'][^>]*>(.*?)</a\s*>",
            m => { var t = Regex.Replace(m.Groups[2].Value, "<[^>]+>", "").Trim(); return t.Length == 0 ? "" : $"[{t}]({m.Groups[1].Value})"; },
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        h = Regex.Replace(h, @"<(br|hr)\b[^>]*>|</(p|div|section|article|tr|table|ul|ol|pre|blockquote|h[1-6]|li)\s*>", "\n", RegexOptions.IgnoreCase);
        h = Regex.Replace(h, @"<[^>]+>", "");
        h = WebUtility.HtmlDecode(h);
        h = Regex.Replace(h, @"[ \t\f\v ]+", " ");
        h = Regex.Replace(h, @" *\n *", "\n");
        h = Regex.Replace(h, @"\n{3,}", "\n\n");
        return h.Trim();
    }
}
