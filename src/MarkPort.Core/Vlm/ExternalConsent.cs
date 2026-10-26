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

using MarkPort.Core.Settings;

namespace MarkPort.Core.Vlm;

public enum ConsentState
{
    /// <summary>The connection stays inside the local network.</summary>
    NotRequired,

    /// <summary>The user agreed to the current notice.</summary>
    Granted,

    /// <summary>The user has not agreed.</summary>
    Missing,

    /// <summary>The user agreed to an older notice; the notice has changed since.</summary>
    Outdated,
}

/// <summary>
/// The consent to send page images outside the local network (FR-046, FR-052, Decision 005): needed by every
/// external command and by every OpenAI-compatible connection whose address is not local. Without it nothing is
/// started and nothing is sent (SC-023).
/// </summary>
public static class ExternalConsent
{
    /// <summary>The version of the notice text. Raise it when the notice changes, so everyone is asked again.</summary>
    public const int CurrentNoticeVersion = 1;

    public static bool IsRequired(ConnectionProfile connection) =>
        connection.Kind == ConnectionKind.Command || !DestinationClassifier.IsLocal(connection.BaseUrl);

    public static ConsentState StateOf(ConnectionProfile connection)
    {
        if (!IsRequired(connection))
        {
            return ConsentState.NotRequired;
        }

        return connection.ExternalConsent switch
        {
            null => ConsentState.Missing,
            { NoticeVersion: var v } when v == CurrentNoticeVersion => ConsentState.Granted,
            _ => ConsentState.Outdated,
        };
    }

    /// <summary>True when the connection may be started or checked: no consent is needed, or it is given and current.</summary>
    public static bool MayUse(ConnectionProfile connection) =>
        StateOf(connection) is ConsentState.NotRequired or ConsentState.Granted;

    public static void Grant(ConnectionProfile connection, DateTimeOffset now)
    {
        if (!IsRequired(connection))
        {
            throw new InvalidOperationException("This connection stays inside the local network and needs no consent.");
        }

        connection.ExternalConsent = new ConsentRecord { At = now, NoticeVersion = CurrentNoticeVersion };
    }

    public static void Revoke(ConnectionProfile connection) => connection.ExternalConsent = null;
}
