using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace KeyShiftBridge;

internal sealed class LocalApi
{
    private const int Port = 43817;
    private static readonly HashSet<string> AllowedOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        "https://xpui.app.spotify.com",
    };
    private readonly AudioController _controller;
    private readonly TcpListener _listener = new(IPAddress.Loopback, Port);
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public LocalApi(AudioController controller) => _controller = controller;

    public void Start()
    {
        _stop = new CancellationTokenSource();
        _listener.Start(8);
        _loop = AcceptLoopAsync(_stop.Token);
    }

    public void Stop()
    {
        _stop?.Cancel();
        _listener.Stop();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _stop?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 2000;
                client.SendTimeout = 2000;
                var stream = client.GetStream();
                var (method, path, headers, body) = await ReadRequestAsync(stream, cancellationToken);
                headers.TryGetValue("origin", out var origin);
                if (!string.IsNullOrEmpty(origin) && !AllowedOrigins.Contains(origin))
                {
                    await RespondAsync(stream, 403, "{\"ok\":false,\"message\":\"Origin not allowed\"}", null, cancellationToken);
                    return;
                }

                if (method == "OPTIONS")
                {
                    await RespondAsync(stream, 204, "", origin, cancellationToken);
                    return;
                }

                if (method == "GET" && path == "/status")
                {
                    await RespondJsonAsync(stream, 200, new
                    {
                        ok = true,
                        enabled = _controller.Enabled,
                        semitones = _controller.Semitones,
                        message = _controller.StatusMessage,
                        outputDevice = _controller.OutputDeviceName,
                        outputMuted = _controller.OutputMuted,
                        outputVolume = _controller.OutputVolume,
                    }, origin, cancellationToken);
                    return;
                }

                if (method == "POST" && path == "/pitch")
                {
                    using var document = JsonDocument.Parse(body);
                    var semitones = document.RootElement.GetProperty("semitones").GetDouble();
                    if (semitones is < -12 or > 12)
                    {
                        await RespondJsonAsync(stream, 400, new { ok = false, message = "Сдвиг должен быть от −12 до +12" }, origin, cancellationToken);
                        return;
                    }
                    _controller.SetSemitones(semitones);
                    await RespondJsonAsync(stream, 200, new { ok = true, semitones = _controller.Semitones }, origin, cancellationToken);
                    return;
                }

                if (method == "POST" && path == "/enabled")
                {
                    using var document = JsonDocument.Parse(body);
                    var enabled = document.RootElement.GetProperty("enabled").GetBoolean();
                    await _controller.SetEnabledAsync(enabled);
                    await RespondJsonAsync(stream, 200, new
                    {
                        ok = !enabled || _controller.Enabled,
                        enabled = _controller.Enabled,
                        semitones = _controller.Semitones,
                        message = _controller.StatusMessage,
                    }, origin, cancellationToken);
                    return;
                }

                await RespondAsync(stream, 404, "{\"ok\":false,\"message\":\"Not found\"}", origin, cancellationToken);
            }
            catch (Exception ex)
            {
                try
                {
                    var stream = client.GetStream();
                    await RespondAsync(stream, 400, JsonSerializer.Serialize(new { ok = false, message = ex.Message }), null, cancellationToken);
                }
                catch { }
            }
        }
    }

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers, byte[] Body)> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var received = new MemoryStream();
        var buffer = new byte[4096];
        var headerEnd = -1;
        while (headerEnd < 0 && received.Length < 8192)
        {
            var bytes = await stream.ReadAsync(buffer, cancellationToken);
            if (bytes == 0) throw new IOException("Пустой HTTP-запрос");
            received.Write(buffer, 0, bytes);
            headerEnd = FindHeaderEnd(received.GetBuffer(), (int)received.Length);
        }
        if (headerEnd < 0) throw new IOException("Слишком большой HTTP-заголовок");

        var all = received.ToArray();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ', 3);
        if (first.Length < 2) throw new IOException("Некорректная строка HTTP-запроса");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var split = line.IndexOf(':');
            if (split > 0) headers[line[..split].Trim()] = line[(split + 1)..].Trim();
        }

        headers.TryGetValue("Content-Length", out var lengthText);
        var contentLength = int.TryParse(lengthText, out var parsedLength) ? parsedLength : 0;
        if (contentLength < 0 || contentLength > 2048) throw new IOException("Слишком большой HTTP-запрос");
        var bodyOffset = headerEnd + 4;
        var bodyAvailable = all.Length - bodyOffset;
        while (bodyAvailable < contentLength)
        {
            var bytes = await stream.ReadAsync(buffer, cancellationToken);
            if (bytes == 0) throw new IOException("Запрос завершился до конца тела");
            received.Write(buffer, 0, bytes);
            all = received.ToArray();
            bodyAvailable = all.Length - bodyOffset;
        }
        return (first[0].ToUpperInvariant(), first[1], headers, all.AsSpan(bodyOffset, contentLength).ToArray());
    }

    private static int FindHeaderEnd(byte[] bytes, int length)
    {
        for (var i = 0; i <= length - 4; i++)
            if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) return i;
        return -1;
    }

    private static async Task RespondJsonAsync(NetworkStream stream, int status, object payload, string? origin, CancellationToken cancellationToken) =>
        await RespondAsync(stream, status, JsonSerializer.Serialize(payload), origin, cancellationToken);

    private static async Task RespondAsync(NetworkStream stream, int status, string body, string? origin, CancellationToken cancellationToken)
    {
        var reason = status switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", _ => "Not Found" };
        var payload = Encoding.UTF8.GetBytes(body);
        var headers = new StringBuilder()
            .Append($"HTTP/1.1 {status} {reason}\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append($"Content-Length: {payload.Length}\r\n")
            .Append("Connection: close\r\n")
            .Append("Cache-Control: no-store\r\n");
        if (!string.IsNullOrEmpty(origin))
        {
            headers.Append($"Access-Control-Allow-Origin: {origin}\r\n")
                .Append("Vary: Origin\r\n")
                .Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n")
                .Append("Access-Control-Allow-Headers: Content-Type\r\n")
                .Append("Access-Control-Allow-Private-Network: true\r\n");
        }
        headers.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken);
        if (payload.Length > 0) await stream.WriteAsync(payload, cancellationToken);
    }
}
