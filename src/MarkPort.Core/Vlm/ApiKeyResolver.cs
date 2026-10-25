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
using System.Security.Cryptography;
using System.Text;
using MarkPort.Core.Logging;
using MarkPort.Core.Settings;

namespace MarkPort.Core.Vlm;

/// <summary>
/// Protects an API key typed on the screen so it can be kept in settings.json: it is encrypted for the current
/// Windows user (DPAPI) and written as <c>dpapi:&lt;Base64&gt;</c> (Decision 002). Another user or PC cannot read it.
/// </summary>
public static class DpapiProtector
{
    public const string Prefix = "dpapi:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MarkPort.ApiKey.v1");

    public static bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static string Protect(string plain)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + System.Convert.ToBase64String(bytes);
    }

    /// <exception cref="CryptographicException">The value was protected for another user or PC, or is damaged.</exception>
    public static string Unprotect(string protectedValue)
    {
        var bytes = System.Convert.FromBase64String(protectedValue[Prefix.Length..]);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
    }
}

/// <param name="Key">The key to send, or <c>null</c> when the connection needs none.</param>
/// <param name="Error">Why the key could not be found, in words for the user; <c>null</c> when it was found (or none is needed).</param>
public sealed record ResolvedKey(string? Key, string? Error);

/// <summary>Finds the API key of a connection (FR-019a, Decision 002).</summary>
public static class ApiKeyResolver
{
    /// <summary>
    /// The environment variable named by <c>apiKeyEnv</c> wins over <c>apiKey</c>, and is read again at every call.
    /// A key that is found is registered, so it is removed from logs, messages and files (FR-023).
    /// </summary>
    public static ResolvedKey Resolve(ConnectionProfile connection, Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= name => Environment.GetEnvironmentVariable(name);

        if (!string.IsNullOrWhiteSpace(connection.ApiKeyEnv))
        {
            var value = readEnvironment(connection.ApiKeyEnv.Trim());
            if (string.IsNullOrEmpty(value))
            {
                return new ResolvedKey(
                    null,
                    Loc.T("The environment variable {0} is not set (the access key of the connection '{1}').", connection.ApiKeyEnv, connection.Name));
            }

            SecretRegistry.Shared.Register(value);
            return new ResolvedKey(value, null);
        }

        if (string.IsNullOrEmpty(connection.ApiKey))
        {
            return new ResolvedKey(null, null);
        }

        if (DpapiProtector.IsProtected(connection.ApiKey))
        {
            try
            {
                var plain = DpapiProtector.Unprotect(connection.ApiKey);
                SecretRegistry.Shared.Register(plain);
                return new ResolvedKey(plain, null);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return new ResolvedKey(
                    null,
                    Loc.T("The access key of the connection '{0}' cannot be read. It may have been saved by another user or on another PC. Enter the key again or use an environment variable.", connection.Name));
            }
        }

        // A key written by hand in settings.json.
        SecretRegistry.Shared.Register(connection.ApiKey);
        return new ResolvedKey(connection.ApiKey, null);
    }

    /// <summary>How a connection gets its key, for display: never the key itself.</summary>
    public static string Describe(ConnectionProfile connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.ApiKeyEnv))
        {
            return Loc.T("environment variable {0}", connection.ApiKeyEnv);
        }

        return string.IsNullOrEmpty(connection.ApiKey) ? Loc.T("none") : Loc.T("saved");
    }
}
