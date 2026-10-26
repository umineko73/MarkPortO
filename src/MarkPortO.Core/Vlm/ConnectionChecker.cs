// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPortO.
//
// MarkPortO is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPortO is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPortO.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using MarkPortO.Core.Localization;
using System.Net;
using MarkPortO.Core.Settings;

namespace MarkPortO.Core.Vlm;

/// <summary>The result of a check: whether it passed, and a sentence for the user (with the reason when it did not).</summary>
public sealed record CheckResult(bool Ok, string Message);

/// <summary>
/// The two checks of an VLM connection (FR-019b, contracts/vlm-openai-compat.md): the connection check (the server can be
/// reached, accepts the key, and has the model) and the image check (the model can read a picture).
/// </summary>
public sealed class ConnectionChecker(VlmClient client, ConnectionProfile connection, Func<string>? codeFactory = null)
{
    /// <summary>
    /// The limit of the answer in the image check. A model that thinks before it answers (Gemma, Qwen and others) spends
    /// its tokens on the thinking first; a small limit leaves the answer empty even though the model reads the picture.
    /// </summary>
    private const int ImageCheckMaxTokens = 2048;

    private const string ImagePrompt = "画像に書かれている文字だけを、そのまま答えてください。";

    private readonly Func<string> _codeFactory = codeFactory ?? TestImageFactory.RandomCode;

    public async Task<CheckResult> CheckConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var models = await client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            if (models.Contains(connection.Model, StringComparer.Ordinal))
            {
                return new CheckResult(true, Loc.T("Connected. The model '{0}' is available.", connection.Model));
            }

            var candidates = string.Join("、", models.Take(5));
            return new CheckResult(
                false,
                candidates.Length > 0
                    ? Loc.T("Connected, but the model '{0}' was not found. Available models: {1}", connection.Model, candidates)
                    : Loc.T("Connected, but the model '{0}' was not found.", connection.Model));
        }
        catch (VlmException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            // The server has no model list: a small text request tells whether it can be reached and accepts the key.
            return await CheckWithTextRequestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (VlmException ex)
        {
            return new CheckResult(false, Describe(ex));
        }
    }

    public async Task<CheckResult> CheckImageAsync(CancellationToken cancellationToken)
    {
        var code = _codeFactory();
        try
        {
            var answer = await client.ChatAsync(ImagePrompt, TestImageFactory.Create(code), maxTokens: ImageCheckMaxTokens, cancellationToken).ConfigureAwait(false);
            if (answer.Truncated && string.IsNullOrWhiteSpace(answer.Text))
            {
                return new CheckResult(
                    false,
                    Loc.T("The answer was cut off at the token limit ({0}) before it came. This may be a model that thinks before answering, with a very long thinking part.", ImageCheckMaxTokens));
            }

            var read = new string(answer.Text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            return read.Contains(code, StringComparison.Ordinal)
                ? new CheckResult(true, Loc.T("The image was read. The model '{0}' can analyze images.", connection.Model))
                : new CheckResult(
                    false,
                    Loc.T("The content of the image could not be read (answer: '{0}'). The model may not be able to analyze images.", Shorten(answer.Text)));
        }
        catch (VlmException ex) when (ex.Status is { } status && (int)status is >= 400 and < 500 && !ex.IsAuthenticationFailure
                                     && status is not (HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests))
        {
            return new CheckResult(false, Loc.T("This model does not accept image input. ({0})", ex.Message));
        }
        catch (VlmException ex)
        {
            return new CheckResult(false, Describe(ex));
        }
    }

    private async Task<CheckResult> CheckWithTextRequestAsync(CancellationToken cancellationToken)
    {
        try
        {
            await client.ChatAsync("ping", png: null, maxTokens: 1, cancellationToken).ConfigureAwait(false);
            return new CheckResult(true, Loc.T("Connected (this server does not list its models, so whether the model exists could not be checked)."));
        }
        catch (VlmException ex)
        {
            return new CheckResult(false, Describe(ex));
        }
    }

    private static string Describe(VlmException ex)
    {
        if (ex.TimedOut)
        {
            return Loc.T("Timed out. {0}", ex.Message);
        }

        if (ex.Unreachable)
        {
            return Loc.T("Could not reach the server. Check the URL and that the server is running. ({0})", ex.Message);
        }

        if (ex.IsAuthenticationFailure)
        {
            return Loc.T("Authentication failed. Check the access key.");
        }

        return Loc.T("Failed. {0}", ex.Message);
    }

    private static string Shorten(string text)
    {
        var flat = text.Replace('\n', ' ').Trim();
        return flat.Length > 40 ? flat[..40] + "…" : flat;
    }
}
