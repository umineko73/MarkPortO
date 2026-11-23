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

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MarkPortO.Core.IO;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Localization;

namespace MarkPortO.Core.Markdown;

/// <summary>Writes the chunks for retrieval of a document as JSON Lines, <c>&lt;name&gt;_chunks.jsonl</c> (FR-152, contracts/chunks-format.md).</summary>
public static class ChunksWriter
{
    /// <summary>The version of the format of a line. It goes up when the meaning of an item changes or an item is taken away, not when one is added.</summary>
    public const int SchemaVersion = 1;

    public const string FailedWarning = "ChunksFailed";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // The text stays readable (Japanese, the signs of Markdown); the file is not for a web page.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    /// <summary>The file of the chunks of a document: the document name plus <c>_chunks.jsonl</c>.</summary>
    public static string FileName(string nameWithoutExtension) =>
        Jobs.OutputNamer.Sanitize(nameWithoutExtension) + "_chunks.jsonl";

    /// <summary>
    /// Writes the file of the chunks when the document asks for it, or removes the file of an earlier run when it does not. A file that cannot be
    /// written is a warning: the Markdown is there, so the document does not fail.
    /// </summary>
    public static void Write(DocumentRecord document) =>
        Write(
            document,
            Path.Combine(document.OutputFolder, FileName(Path.GetFileNameWithoutExtension(document.SourcePath))),
            MarkdownRenderer.MarkdownFileName(Path.GetFileNameWithoutExtension(document.SourcePath)),
            pages: null);

    /// <summary>Writes (or removes) one file of chunks: that of the whole document, or that of one sheet (FR-153).</summary>
    /// <param name="markdown">The name of the Markdown file that the chunks are of.</param>
    /// <param name="pages">Only these pages; null for every page.</param>
    /// <param name="oneOfMany">One of the files of the sheets: a file written does not take away the warning of another that was not
    /// (the caller takes it away before the first).</param>
    public static void Write(DocumentRecord document, string path, string markdown, IReadOnlySet<int>? pages, bool oneOfMany = false)
    {
        if (!document.Chunks)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return;
        }

        try
        {
            AtomicFile.WriteAllText(path, Render(document, markdown, pages));
            if (!oneOfMany)
            {
                document.Warnings.RemoveAll(w => w.Code == FailedWarning);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!document.Warnings.Any(w => w.Code == FailedWarning))
            {
                document.Warnings.Add(new Warning(FailedWarning, Loc.T("The chunks for RAG could not be written: {0}", ex.Message)));
            }
        }
    }

    /// <summary>
    /// Where the document is in the folder that was given, with <c>/</c> (as the list of files shows it, FR-013a): <c>v1.2/設計書.docx</c>. A
    /// document given on its own is its file name. Never a path from the root of a drive: the folders of the user are not written (FR-152).
    /// </summary>
    internal static string RelativePath(DocumentRecord document)
    {
        var name = document.DisplayName;
        if (name.Length == 0 || Path.IsPathRooted(name) || name.Split('\\', '/').Contains(".."))
        {
            name = Path.GetFileName(document.SourcePath);
        }

        return name.Replace('\\', '/');
    }

    /// <summary>The lines of the file: one chunk on each line, each line ends with LF. A document without chunks gives an empty text.</summary>
    public static string Render(DocumentRecord document) =>
        Render(document, MarkdownRenderer.MarkdownFileName(Path.GetFileNameWithoutExtension(document.SourcePath)), pages: null);

    private static string Render(DocumentRecord document, string markdown, IReadOnlySet<int>? pages)
    {
        var chunks = ChunkBuilder.Build(document, pages);
        var path = RelativePath(document);
        var source = Path.GetFileName(document.SourcePath);
        var modified = document.Fingerprint.LastWriteUtcTicks > 0
            ? new DateTime(document.Fingerprint.LastWriteUtcTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : null;
        var images = document.Pages.Where(p => p.ImageFile.Length > 0).ToDictionary(p => p.Index, p => p.ImageFile);

        var builder = new StringBuilder();
        using var stream = new MemoryStream();
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            stream.SetLength(0);
            using (var json = new Utf8JsonWriter(stream, WriterOptions))
            {
                json.WriteStartObject();
                json.WriteNumber("schemaVersion", SchemaVersion);
                json.WriteString("id", chunk.Id);
                json.WriteString("prev", i > 0 ? chunks[i - 1].Id : null);
                json.WriteString("next", i + 1 < chunks.Count ? chunks[i + 1].Id : null);
                json.WriteString("source", source);
                json.WriteString("path", path);
                json.WriteString("sourceModified", modified);
                json.WriteString("markdown", markdown);
                json.WriteStartArray("headings");
                foreach (var heading in chunk.Headings)
                {
                    json.WriteStringValue(heading);
                }

                json.WriteEndArray();
                json.WriteString("kind", chunk.Kind);
                if (chunk.FirstPage is { } first)
                {
                    json.WriteStartArray("pages");
                    json.WriteNumberValue(first);
                    json.WriteNumberValue(chunk.LastPage ?? first);
                    json.WriteEndArray();
                }
                else
                {
                    json.WriteNull("pages");
                }

                json.WriteStartArray("pageImages");
                if (chunk.FirstPage is { } from)
                {
                    for (var page = from; page <= (chunk.LastPage ?? from); page++)
                    {
                        if (images.TryGetValue(page, out var image))
                        {
                            json.WriteStringValue(image);
                        }
                    }
                }

                json.WriteEndArray();
                if (chunk.Sheet is not null)
                {
                    json.WriteString("sheet", chunk.Sheet);
                }

                json.WriteString("text", chunk.Text);
                json.WriteEndObject();
            }

            builder.Append(Encoding.UTF8.GetString(stream.ToArray())).Append('\n');
        }

        return builder.ToString();
    }
}
