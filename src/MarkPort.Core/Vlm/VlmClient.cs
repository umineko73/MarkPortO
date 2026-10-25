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

using MarkPort.Core.Localization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Logging;
using MarkPort.Core.Settings;

namespace MarkPort.Core.Vlm;

/// <summary>An VLM call failed. The message never contains the API key.</summary>
public sealed class VlmException : Exception
{
    public VlmException(string message, HttpStatusCode? status = null, bool timedOut = false, bool unreachable = false)
        : base(SecretRegistry.Shared.Redact(message))
    {
        Status = status;
        TimedOut = timedOut;
        Unreachable = unreachable;
    }

    public HttpStatusCode? Status { get; }

    public bool TimedOut { get; }

    public bool Unreachable { get; }

    public bool IsAuthenticationFailure => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

/// <summary>The answer of the VLM to one page, and what it cost.</summary>
/// <param name="Truncated">The server stopped because of the token limit (<c>finish_reason</c> is <c>length</c>).</param>
public sealed record VlmResponse(string Text, UsageInfo Usage, bool Truncated = false);

/// <summary>Reads the text of one page image. A fake is used in tests.</summary>
public interface IVlmReader
{
    Task<VlmResponse> ReadAsync(string prompt, byte[] png, CancellationToken cancellationToken);
}

/// <summary>
/// Talks to an OpenAI-compatible server (<c>contracts/vlm-openai-compat.md</c>). The timeout of the connection applies
/// to the whole of one request; 429 and 503 are tried once more after a wait.
/// </summary>
public sealed class VlmClient : IVlmReader, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ConnectionProfile _connection;
    private readonly HttpClient _http;

    public VlmClient(ConnectionProfile connection, string? apiKey, HttpMessageHandler? handler = null)
    {
        _connection = connection;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan; // the connection's own timeout is applied per request below
        if (!string.IsNullOrEmpty(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    /// <summary>How long to wait before trying 429/503 once more.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public string BaseUrl => _connection.BaseUrl.TrimEnd('/');

    public Task<VlmResponse> ReadAsync(string prompt, byte[] png, CancellationToken cancellationToken) =>
        ChatAsync(prompt, png, maxTokens: null, cancellationToken);

    /// <summary>A chat request with the text, and with the image when <paramref name="png"/> is given.</summary>
    public async Task<VlmResponse> ChatAsync(string text, byte[]? png, int? maxTokens, CancellationToken cancellationToken)
    {
        var content = new List<object> { new { type = "text", text } };
        if (png is not null)
        {
            content.Add(new { type = "image_url", image_url = new { url = "data:image/png;base64," + System.Convert.ToBase64String(png) } });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = _connection.Model,
            ["messages"] = new[] { new { role = "user", content } },
            ["temperature"] = 0,
        };
        if (maxTokens is { } max)
        {
            body["max_tokens"] = max;
        }

        var json = JsonSerializer.Serialize(body, Json);
        using var response = await SendAsync(HttpMethod.Post, BaseUrl + "/chat/completions", json, cancellationToken).ConfigureAwait(false);
        var answer = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(answer);
    }

    /// <summary>The ids of the models of the server (<c>GET /models</c>).</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, BaseUrl + "/models", null, cancellationToken).ConfigureAwait(false);
        var answer = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(answer);
            return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? [.. data.EnumerateArray().Select(m => m.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty).Where(id => id.Length > 0)]
                : [];
        }
        catch (JsonException)
        {
            throw new VlmException(Loc.T("The server answered /models, but not in the OpenAI format."));
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? json, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_connection.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (json is not null)
                {
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new VlmException(Loc.T("The server did not answer within {0} seconds.", _connection.TimeoutSeconds), timedOut: true);
            }
            catch (HttpRequestException ex)
            {
                throw new VlmException(Loc.T("Cannot reach {0}: {1}", url, ex.Message), unreachable: true);
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = response.StatusCode;
            var detail = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();

            // Busy or rate limited: wait and try once more, then give up.
            if (attempt == 1 && status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw new VlmException($"HTTP {(int)status} {status}. {detail}".Trim(), status);
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return text.Length > 300 ? text[..300] + "..." : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return string.Empty;
        }
    }

    private static VlmResponse Parse(string answer)
    {
        try
        {
            using var doc = JsonDocument.Parse(answer);
            var root = doc.RootElement;
            var message = root.GetProperty("choices")[0].GetProperty("message");
            var text = message.TryGetProperty("content", out var content) ? ContentText(content) : string.Empty;

            int? Number(JsonElement element, string name) =>
                element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;

            var usage = UsageInfo.Unavailable;
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                usage = new UsageInfo(Number(u, "prompt_tokens"), Number(u, "completion_tokens"), Number(u, "total_tokens"));
            }

            var truncated = root.GetProperty("choices")[0].TryGetProperty("finish_reason", out var reason)
                            && reason.ValueKind == JsonValueKind.String && reason.GetString() == "length";
            return new VlmResponse(text, usage, truncated);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new VlmException(Loc.T("The server answered, but not in the OpenAI chat format."));
        }
    }

    /// <summary>The content is a string, or a list of parts of which the text parts are joined.</summary>
    private static string ContentText(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? string.Empty,
        JsonValueKind.Array => string.Concat(content.EnumerateArray()
            .Select(p => p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)
            .Where(t => t is not null)),
        _ => string.Empty,
    };
}
