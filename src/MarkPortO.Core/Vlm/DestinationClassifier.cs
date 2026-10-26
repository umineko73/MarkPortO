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

using System.Net;
using System.Net.Sockets;

namespace MarkPortO.Core.Vlm;

/// <summary>
/// Tells whether the address of a connection is inside the local network (FR-046): then page images do not need the
/// consent to be sent outside. Anything that cannot be read as such an address counts as outside.
/// </summary>
public static class DestinationClassifier
{
    private static readonly string[] LocalSuffixes = [".local", ".lan", ".internal", ".localhost"];

    public static bool IsLocal(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var host = uri.Host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
        {
            return false;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            return IsLocal(address);
        }

        // A name without a dot ("lmstudio") is a name inside the network; so are the usual local suffixes.
        return host == "localhost"
            || !host.Contains('.')
            || LocalSuffixes.Any(host.EndsWith);
    }

    private static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        // IPv6: link-local (fe80::/10) and unique local (fc00::/7).
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && ((bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) || (bytes[0] & 0xfe) == 0xfc);
    }
}
