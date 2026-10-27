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
using MarkPortO.Core.Settings;

namespace MarkPortO.Core.Vlm;

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
    public const int CurrentNoticeVersion = 2;

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

    /// <summary>Where the page images go: the service behind the command, or the host of the address.</summary>
    public static string Destination(ConnectionProfile connection)
    {
        if (connection.Kind == ConnectionKind.Command)
        {
            var command = (connection.Command ?? CommandKind.Claude).ToString().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(connection.Model)
                ? Loc.T("the service behind the command '{0}'", command)
                : Loc.T("the service behind the command '{0}' (model {1})", command, connection.Model.Trim());
        }

        return Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : connection.BaseUrl;
    }

    /// <summary>The notice the user agrees to (<see cref="CurrentNoticeVersion"/>): where the images and the text of a page go, the cost, the information a command adds.</summary>
    public static IReadOnlyList<string> Notice(ConnectionProfile connection)
    {
        var lines = new List<string>
        {
            Loc.T("The page images of your documents will be sent outside the local network, to: {0}.", Destination(connection)),
            Loc.T("If a prompt contains {pagetext}, the text of the page that was taken from the document is sent with the image."),
            Loc.T("Usage charges may apply to your account."),
        };
        if (connection.Kind == ConnectionKind.Command)
        {
            lines.Add(Loc.T("A command may also send information about your environment (for example the email address of your account) to its provider."));
        }

        lines.Add(Loc.T("Do not use this connection for confidential documents."));
        return lines;
    }
}
