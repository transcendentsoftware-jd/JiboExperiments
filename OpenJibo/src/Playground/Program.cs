using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Playground;

Console.Write("Enter Jibo IP: ");
var jiboIp = (Console.ReadLine() ?? "").Trim();

if (string.IsNullOrWhiteSpace(jiboIp))
{
    Console.WriteLine("No IP entered.");
    return;
}

var baseHttp = $"http://{jiboIp}:8088";
var ttsHttp = $"http://{jiboIp}:8089";
var wsUri = new Uri($"ws://{jiboIp}:8088/simple_port");

using var http = new HttpClient();
using var cts = new CancellationTokenSource();

Console.WriteLine($"Connecting to Jibo at {jiboIp}...");
Console.WriteLine("Press Ctrl+C to quit.");

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

while (!cts.IsCancellationRequested)
{
    var taskId = $"DEBUG:demo-{Guid.NewGuid():N}";
    var requestId = $"stt_start_{Guid.NewGuid():N}";

    try
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(wsUri, cts.Token);
        Console.WriteLine("WebSocket connected.");

        var utteranceTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var wsReaderTask = Task.Run(async () =>
        {
            var buffer = new byte[8192];

            while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                using var ms = new MemoryStream();

                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine("WebSocket closed by server.");
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.ToArray());

                AsrEvent? evt;
                try
                {
                    evt = JsonSerializer.Deserialize<AsrEvent>(json);
                }
                catch
                {
                    Console.WriteLine($"Non-JSON WS message: {json}");
                    continue;
                }

                if (evt == null)
                    continue;

                if (evt.TaskId != taskId)
                    continue;

                Console.WriteLine($"[{evt.EventType}] {json}");

                if (evt.EventType != "speech_to_text_final") continue;
                var best = PickBestUtterance(evt.Utterances);
                if (string.IsNullOrWhiteSpace(best)) continue;
                utteranceTcs.TrySetResult(best);
                return;
            }
        }, cts.Token);

        var startPayload = new
        {
            command = "start",
            task_id = taskId,
            audio_source_id = "alsa1",
            hotphrase = "none",
            speech_to_text = true,
            request_id = requestId
        };

        var startResp = await http.PostAsJsonAsync($"{baseHttp}/asr_simple_interface", startPayload, cts.Token);
        var startBody = await startResp.Content.ReadAsStringAsync(cts.Token);

        Console.WriteLine($"ASR start: {(int)startResp.StatusCode} {startResp.ReasonPhrase}");
        Console.WriteLine(startBody);

        if (!startResp.IsSuccessStatusCode)
            continue;

        Console.WriteLine("Speak now...");

        var completed = await Task.WhenAny(utteranceTcs.Task, Task.Delay(TimeSpan.FromSeconds(15), cts.Token));

        if (completed != utteranceTcs.Task)
        {
            Console.WriteLine("Timed out waiting for speech_to_text_final.");
        }
        else
        {
            var heard = utteranceTcs.Task.Result;
            Console.WriteLine($"Heard: {heard}");

            var reply = BuildReply(heard);
            Console.WriteLine($"Reply: {reply}");

            var ttsPayload = new
            {
                prompt = reply,
                locale = "en-us",
                voice = "griffin",
                mode = "text",
                outputMode = "stream"
            };

            var ttsResp = await http.PostAsJsonAsync($"{ttsHttp}/tts_speak", ttsPayload, cts.Token);
            var ttsBody = await ttsResp.Content.ReadAsStringAsync(cts.Token);

            Console.WriteLine($"TTS: {(int)ttsResp.StatusCode} {ttsResp.ReasonPhrase}");
            if (!string.IsNullOrWhiteSpace(ttsBody))
                Console.WriteLine(ttsBody);
        }

        var stopPayload = new
        {
            command = "stop",
            task_id = taskId,
            request_id = $"stt_stop_{Guid.NewGuid():N}"
        };

        var stopResp = await http.PostAsJsonAsync($"{baseHttp}/asr_simple_interface", stopPayload, cts.Token);
        _ = await stopResp.Content.ReadAsStringAsync(cts.Token);

        Console.WriteLine("STT task stopped.");
        Console.WriteLine();
        Console.WriteLine("Press Enter to run another round, or Ctrl+C to quit.");
        Console.ReadLine();
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
        Console.WriteLine("Retrying in 2 seconds...");
        await Task.Delay(2000, cts.Token);
    }
}

return;

static string PickBestUtterance(List<AsrUtterance>? utterances)
{
    if (utterances == null || utterances.Count == 0)
        return "";

    var cleaned = utterances
        .Select(u => NormalizeUtterance(u.Utterance))
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(s => s.Length)
        .ToList();

    return cleaned.FirstOrDefault() ?? "";
}

static string NormalizeUtterance(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
        return "";

    var s = text.Trim();

    // Very light cleanup for occasional weird leading duplication like "wWhat"
    if (s.Length >= 2 && char.ToLowerInvariant(s[0]) == char.ToLowerInvariant(s[1]))
        s = s[1..];

    return s;
}

static string BuildReply(string heard)
{
    var text = heard.Trim().ToLowerInvariant();

    if (text.Contains("time"))
        return $"It is {DateTime.Now:hh:mm tt}.";

    if (text.Contains("hello") || text.Contains("hi"))
        return "Hello! I heard you loud and clear.";

    return text.Contains("your name") ? "I am Jibo, running with a local demo bridge." : $"You said: {heard}";
}