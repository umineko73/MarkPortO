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

namespace MarkPortO.Cli.Tests;

/// <summary>
/// An address on this computer that nothing listens on, so a connection is refused at once. A fixed port such as
/// <c>localhost:1</c> can be dropped silently by a firewall, which makes the test wait for the timeout instead.
/// </summary>
public static class ClosedPort
{
    public static string Url
    {
        get
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return $"http://127.0.0.1:{port}/v1";
        }
    }
}
