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

using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Engines;

/// <summary>Whether an engine can be used on this computer, and if not, why.</summary>
public sealed record EngineAvailability(bool IsAvailable, string? Reason)
{
    public static EngineAvailability Available { get; } = new(true, null);

    public static EngineAvailability Unavailable(string reason) => new(false, reason);
}

/// <param name="SourcePath">The document to convert. It is only read; it is never changed.</param>
/// <param name="Kind">docx, xlsx or pptx.</param>
/// <param name="OutputPdfPath">Where the PDF is written.</param>
/// <param name="Timeout">The engine is stopped when this time passes.</param>
public sealed record PdfExportRequest(string SourcePath, DocumentKind Kind, string OutputPdfPath, TimeSpan Timeout)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The one thing that differs between Microsoft Office and LibreOffice: turning a document into a PDF.
/// Everything after that (page images, page assignment, text) is the same for both (Decision 001).
/// </summary>
public interface IDocumentEngine
{
    EngineKind Kind { get; }

    EngineAvailability CheckAvailability();

    /// <exception cref="EngineException">The document could not be converted.</exception>
    Task ExportPdfAsync(PdfExportRequest request, CancellationToken cancellationToken);
}

/// <summary>A document could not be converted by the engine (corrupt, password protected, timed out ...).</summary>
public class EngineException : Exception
{
    public EngineException(string message)
        : base(message)
    {
    }

    public EngineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The document needs a password; no dialog is shown and the file is reported as failed.</summary>
    public bool IsPasswordProtected { get; init; }

    public bool TimedOut { get; init; }
}
