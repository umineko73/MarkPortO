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

namespace MarkPortO.Core.Logging;

/// <summary>
/// Keeps the secret values (API keys, values read from environment variables) that must never
/// appear in logs, screens, output files, or results. <see cref="Redact"/> replaces them with <c>***</c>.
/// </summary>
public sealed class SecretRegistry
{
    /// <summary>Values shorter than this are not registered: replacing them would damage ordinary text.</summary>
    public const int MinimumSecretLength = 4;

    public const string Mask = "***";

    private readonly object _gate = new();
    private string[] _secrets = [];

    /// <summary>Process-wide registry used by components that have no injected registry.</summary>
    public static SecretRegistry Shared { get; } = new();

    public void Register(string? secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < MinimumSecretLength)
        {
            return;
        }

        lock (_gate)
        {
            if (Array.IndexOf(_secrets, secret) >= 0)
            {
                return;
            }

            // Longer secrets first, so a secret that contains another one is replaced as a whole.
            var updated = new List<string>(_secrets) { secret };
            updated.Sort((a, b) => b.Length.CompareTo(a.Length));
            _secrets = [.. updated];
        }
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        string[] snapshot;
        lock (_gate)
        {
            snapshot = _secrets;
        }

        var result = text;
        foreach (var secret in snapshot)
        {
            result = result.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return result;
    }
}
