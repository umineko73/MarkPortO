// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPort.
//
// MarkPort is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPort is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPort.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MarkPort.Core.Tests.Fixtures;

/// <summary>A request the fake server received.</summary>
public sealed record RecordedRequest(string Method, string Path, string? Authorization, string Body)
{
    public bool HasImage => Body.Contains("image_url", StringComparison.Ordinal);

    public string? Model => Json("model");

    private string? Json(string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(Body);
            return doc.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// A small OpenAI-compatible server on localhost for tests: <c>GET /v1/models</c> and <c>POST /v1/chat/completions</c>.
/// It can be switched to behave like servers that differ: no model list, no usage, no image input, busy, slow, strict key.
/// </summary>
public sealed class FakeOpenAiServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _busyLeft;

    public FakeOpenAiServer()
    {
        var port = FreePort();
        BaseUrl = $"http://localhost:{port}/v1";
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }

    public List<RecordedRequest> Requests { get; } = [];

    public IReadOnlyList<string> Models { get; set; } = ["vision-model", "other-model"];

    /// <summary>False: <c>/models</c> answers 404, as some local servers do.</summary>
    public bool SupportsModelList { get; set; } = true;

    /// <summary>False: a request with an image is refused with 400, as a text-only model does.</summary>
    public bool SupportsImages { get; set; } = true;

    /// <summary>False: the answer has no <c>usage</c>.</summary>
    public bool ReportsUsage { get; set; } = true;

    /// <summary>If set, requests must carry <c>Authorization: Bearer &lt;this&gt;</c>; others get 401.</summary>
    public string? RequiredKey { get; set; }

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>A delay for chat requests only (the model list answers at once).</summary>
    public TimeSpan ChatDelay { get; set; } = TimeSpan.Zero;

    /// <summary>The next this-many chat requests are answered with 429 (busy).</summary>
    public int BusyResponses
    {
        get => _busyLeft;
        set => _busyLeft = value;
    }

    /// <summary>The code a vision model "reads" from the image check.</summary>
    public string ImageCode { get; set; } = "AB23";

    /// <summary>The answer to a page: given the prompt text. Default echoes the prompt.</summary>
    public Func<string, string> PageReply { get; set; } = prompt => "読み取り結果: " + prompt[..Math.Min(12, prompt.Length)];

    /// <summary>
    /// Given the number of the chat request (1, 2, ...), returns true to answer it with a server error (500).
    /// </summary>
    public Func<int, bool>? FailChatRequest { get; set; }

    private int _chatCount;

    /// <summary>
    /// A model that thinks before it answers (reasoning tokens). When the request limits the answer
    /// (<c>max_tokens</c>) to no more than this, the thinking uses it all: the answer is empty and the reason is <c>length</c>.
    /// </summary>
    public int ThinkingTokens { get; set; }

    /// <summary>
    /// Given the number of the chat request (1, 2, ...), returns true to make the server stall on it for a few seconds,
    /// so that a client with a short timeout gives up (a model that hangs on one request).
    /// </summary>
    public Func<int, bool>? StallChatRequest { get; set; }

    public int PromptTokens { get; set; } = 100;

    public int CompletionTokens { get; set; } = 20;

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // the listener was closed while waiting
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var recorded = new RecordedRequest(request.HttpMethod, request.Url!.AbsolutePath, request.Headers["Authorization"], body);
            lock (Requests)
            {
                Requests.Add(recorded);
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, _stop.Token);
            }

            if (RequiredKey is not null && recorded.Authorization != "Bearer " + RequiredKey)
            {
                await Respond(context, 401, """{"error":{"message":"invalid api key"}}""");
                return;
            }

            if (recorded.Method == "GET" && recorded.Path.EndsWith("/models", StringComparison.Ordinal))
            {
                if (!SupportsModelList)
                {
                    await Respond(context, 404, """{"error":"not found"}""");
                    return;
                }

                var data = string.Join(",", Models.Select(m => $"{{\"id\":\"{m}\",\"object\":\"model\"}}"));
                await Respond(context, 200, $"{{\"object\":\"list\",\"data\":[{data}]}}");
                return;
            }

            if (recorded.Method == "POST" && recorded.Path.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                await ChatAsync(context, recorded);
                return;
            }

            await Respond(context, 404, "{}");
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // the client went away or the server is stopping
        }
    }

    private async Task ChatAsync(HttpListenerContext context, RecordedRequest request)
    {
        if (Interlocked.Decrement(ref _busyLeft) >= 0)
        {
            await Respond(context, 429, """{"error":"busy"}""");
            return;
        }

        Interlocked.Exchange(ref _busyLeft, 0);

        if (ChatDelay > TimeSpan.Zero)
        {
            await Task.Delay(ChatDelay, _stop.Token);
        }

        var number = Interlocked.Increment(ref _chatCount);
        if (StallChatRequest?.Invoke(number) == true)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), _stop.Token);
        }

        if (FailChatRequest?.Invoke(number) == true)
        {
            await Respond(context, 500, """{"error":"internal error"}""");
            return;
        }

        if (request.HasImage && !SupportsImages)
        {
            await Respond(context, 400, """{"error":{"message":"this model does not support image input"}}""");
            return;
        }

        using var doc = JsonDocument.Parse(request.Body);
        var parts = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        var prompt = parts.ValueKind == JsonValueKind.Array
            ? string.Concat(parts.EnumerateArray().Where(p => p.TryGetProperty("text", out _)).Select(p => p.GetProperty("text").GetString()))
            : parts.GetString() ?? string.Empty;

        // The image check asks for the letters on the picture; a vision model answers with them.
        var answer = request.HasImage && prompt.Contains("画像に書かれている文字", StringComparison.Ordinal)
            ? ImageCode
            : PageReply(prompt);

        var finishReason = "stop";
        if (ThinkingTokens > 0 && doc.RootElement.TryGetProperty("max_tokens", out var limit) && limit.GetInt32() <= ThinkingTokens)
        {
            answer = string.Empty;
            finishReason = "length";
        }

        var usage = ReportsUsage
            ? $",\"usage\":{{\"prompt_tokens\":{PromptTokens},\"completion_tokens\":{CompletionTokens},\"total_tokens\":{PromptTokens + CompletionTokens}}}"
            : string.Empty;
        var json = $"{{\"id\":\"x\",\"choices\":[{{\"index\":0,\"message\":{{\"role\":\"assistant\",\"content\":{JsonSerializer.Serialize(answer)}}},\"finish_reason\":\"{finishReason}\"}}]{usage}}}";
        await Respond(context, 200, json);
    }

    private static async Task Respond(HttpListenerContext context, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
}
