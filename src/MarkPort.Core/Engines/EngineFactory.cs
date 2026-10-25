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

using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Settings;

namespace MarkPort.Core.Engines;

/// <summary>Creates the engine named in the settings. The screen and the command line both use it.</summary>
public static class EngineFactory
{
    public static IDocumentEngine Create(EngineSettings settings) => settings.Kind switch
    {
        EngineKind.LibreOffice => new LibreOfficeEngine(
            string.IsNullOrWhiteSpace(settings.LibreOfficePath) ? null : settings.LibreOfficePath),

        EngineKind.Office => new OfficeEngine(),

        _ => throw new ArgumentOutOfRangeException(nameof(settings)),
    };
}

/// <summary>An engine that cannot be used; it explains why, so the run stops before anything is written.</summary>
public sealed class UnavailableEngine(EngineKind kind, string reason) : IDocumentEngine
{
    public EngineKind Kind { get; } = kind;

    public EngineAvailability CheckAvailability() => EngineAvailability.Unavailable(reason);

    public Task ExportPdfAsync(PdfExportRequest request, CancellationToken cancellationToken) =>
        throw new EngineException(reason);
}
