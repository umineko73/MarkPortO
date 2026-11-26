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
using System.Text.Encodings.Web;
using System.Text.Json;
using MarkPortO.Core.Convert;
using MarkPortO.Core.IO;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Localization;

namespace MarkPortO.Core.Markdown;

/// <summary>
/// The record of the conversion of one document, <c>conversion.json</c> in its folder (FR-155, contracts/conversion-json.md): the input,
/// the conditions and the state, so that the folder alone tells what it is. A copy: the state of the job under <c>.markporto</c> is what
/// counts. It never holds a key, the path of a command, or the text of a prompt.
/// </summary>
public static class ConversionRecord
{
    public const string FileName = "conversion.json";

    /// <summary>The version of the form. It goes up when the meaning of an item changes or an item is taken away, not when one is added.</summary>
    public const int SchemaVersion = 1;

    public const string FailedWarning = "ConversionRecordFailed";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // readable Japanese and paths
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
    };

    /// <summary>
    /// Writes the record into the document folder. A record that cannot be written is a warning of the document: the conversion goes on.
    /// Nothing is written before the folder of the document is known.
    /// </summary>
    public static void Write(JobState job, DocumentRecord document)
    {
        if (string.IsNullOrEmpty(document.OutputFolder))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(document.OutputFolder);
            AtomicFile.WriteAllText(Path.Combine(document.OutputFolder, FileName), Render(job, document));
            document.Warnings.RemoveAll(w => w.Code == FailedWarning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!document.Warnings.Any(w => w.Code == FailedWarning))
            {
                document.Warnings.Add(new Warning(FailedWarning, Loc.T("The record of the conversion (conversion.json) could not be written: {0}", ex.Message)));
            }
        }
    }

    /// <summary>
    /// What changes the record (the steps, a failure, the files): it is written again only when this changes, not at every page read.
    /// </summary>
    public static string Signature(DocumentRecord document) =>
        string.Join('|', document.Step1, document.Step2, document.Step3, document.Error?.Step, document.SheetFiles?.Count, document.OutputFolder);

    /// <summary>The JSON of the record (contracts/conversion-json.md).</summary>
    public static string Render(JobState job, DocumentRecord document)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            var o = job.Options;
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", SchemaVersion);
            w.WriteString("app", "MarkPortO " + AppVersion.Current);

            w.WriteStartObject("source");
            w.WriteString("path", document.SourcePath);
            w.WriteString("relativePath", ChunksWriter.RelativePath(document));
            w.WriteString("kind", Lower(document.Kind));
            w.WriteNumber("size", document.Fingerprint.Size);
            WriteTime(w, "lastWriteTime", document.Fingerprint.LastWriteUtcTicks);
            w.WriteEndObject();

            w.WriteString("outputRoot", job.OutputRoot);
            w.WriteString("jobId", job.JobId.ToString("D"));
            w.WriteString("docId", document.DocId);
            w.WriteString("startedAt", job.CreatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            w.WriteString("updatedAt", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            w.WriteString("status", Status(document));

            w.WriteStartObject("steps");
            w.WriteString("step1", Lower(document.Step1));
            w.WriteString("step2", Lower(document.Step2));
            w.WriteString("step3", Lower(document.Step3));
            w.WriteEndObject();

            w.WriteStartObject("pages");
            w.WriteNumber("total", document.Pages.Count);
            w.WriteNumber("step3Done", document.Pages.Count(p => p.Step3 == PageStep3Status.Done));
            w.WriteNumber("step3Failed", document.Pages.Count(p => p.Step3 == PageStep3Status.Failed));
            w.WriteEndObject();

            if (document.Error is { } error)
            {
                w.WriteStartObject("error");
                w.WriteString("step", "step" + error.Step.ToString(CultureInfo.InvariantCulture));
                w.WriteString("message", error.Message);
                w.WriteEndObject();
            }
            else
            {
                w.WriteNull("error");
            }

            w.WriteNumber("warnings", document.Warnings.Count(x => x.Code != FailedWarning));
            WriteFiles(w, document);
            WriteConditions(w, job, document, o);
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary><c>running</c> / <c>completed</c> / <c>failed</c> / <c>interrupted</c>.</summary>
    public static string Status(DocumentRecord document)
    {
        if (document.IsFailed())
        {
            return "failed";
        }

        if (document.IsDone())
        {
            return "completed";
        }

        return document.Step1 == StepStatus.Interrupted || document.Step2 == StepStatus.Interrupted || document.Step3 == StepStatus.Interrupted
            ? "interrupted"
            : "running";
    }

    private static void WriteFiles(Utf8JsonWriter w, DocumentRecord document)
    {
        var name = Path.GetFileNameWithoutExtension(document.SourcePath);
        var folder = document.OutputFolder;
        bool There(string file) => File.Exists(Path.Combine(folder, file));

        w.WriteStartObject("files");
        var sheets = MarkdownRenderer.SheetOutputs(document);
        var markdown = sheets.Count > 0 ? sheets.Select(s => s.BaseName + ".md").ToList() : [MarkdownRenderer.MarkdownFileName(name)];
        var vlm = sheets.Count > 0 ? sheets.Select(s => s.BaseName + "_vlm.md").ToList() : [MarkdownRenderer.VlmFileName(name)];
        var chunks = sheets.Count > 0 ? sheets.Select(s => s.BaseName + "_chunks.jsonl").ToList() : [ChunksWriter.FileName(name)];
        WriteNames(w, "markdown", markdown.Where(There));
        WriteNames(w, "vlm", vlm.Where(There));
        WriteNames(w, "chunks", chunks.Where(There));
        var log = name + ".log";
        if (There(log))
        {
            w.WriteString("log", log);
        }
        else
        {
            w.WriteNull("log");
        }

        if (sheets.Count > 0)
        {
            w.WriteStartArray("sheets");
            foreach (var sheet in sheets)
            {
                w.WriteStartObject();
                w.WriteString("sheet", sheet.Sheet);
                w.WriteString("markdown", sheet.BaseName + ".md");
                WriteNameOrNull(w, "vlm", There(sheet.BaseName + "_vlm.md") ? sheet.BaseName + "_vlm.md" : null);
                WriteNameOrNull(w, "chunks", There(sheet.BaseName + "_chunks.jsonl") ? sheet.BaseName + "_chunks.jsonl" : null);
                w.WriteStartArray("pages");
                foreach (var page in sheet.Pages.Order())
                {
                    w.WriteNumberValue(page);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }
        else
        {
            w.WriteNull("sheets");
        }

        w.WriteEndObject();
    }

    private static void WriteConditions(Utf8JsonWriter w, JobState job, DocumentRecord document, OptionsSnapshot o)
    {
        w.WriteStartObject("conditions");
        WriteNameOrNull(w, "preset", document.Preset);
        w.WriteString("engine", Lower(o.Engine));
        w.WriteNumber("dpi", o.Dpi);
        w.WriteBoolean("step2", document.Step2 != StepStatus.NotApplicable);
        w.WriteBoolean("step3", document.Step3 != StepStatus.NotApplicable);
        w.WriteString("step3Mode", Lower(document.Step3Mode));
        if (job.Step3Connection is { } connection && document.Step3 != StepStatus.NotApplicable)
        {
            w.WriteStartObject("step3Connection");
            w.WriteString("kind", connection.Kind);
            WriteNameOrNull(w, "model", document.VlmModel ?? connection.Model); // never the command or a key
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("step3Connection");
        }

        w.WriteStartObject("promptNames");
        w.WriteString("docx", o.PromptNames.Docx);
        w.WriteString("xlsx", o.PromptNames.Xlsx);
        w.WriteString("pptx", o.PromptNames.Pptx);
        w.WriteString("pdf", o.PromptNames.Pdf);
        w.WriteEndObject();
        w.WriteString("pageImage", Lower(document.PageImage));
        w.WriteBoolean("vlmSeparate", document.VlmSeparate);
        w.WriteString("step1Tables", Lower(document.Step1Tables));
        w.WriteBoolean("dropRepeatedTables", document.DropRepeatedTables);
        w.WriteBoolean("structureNote", document.StructureNote);
        w.WriteBoolean("chunks", document.Chunks);
        w.WriteBoolean("excelPerSheet", document.ExcelPerSheet);
        w.WriteString("excelRange", Lower(o.ExcelRange));
        w.WriteBoolean("includeHiddenSheets", o.IncludeHiddenSheets);
        w.WriteBoolean("includeHiddenSlides", o.IncludeHiddenSlides);
        w.WriteBoolean("includeOffSlideText", o.IncludeOffSlideText);
        w.WriteBoolean("extractPictures", o.ExtractPictures && document.ExtractPictures); // the condition, not the folder of cut-out shapes
        w.WriteBoolean("extractShapes", o.ExtractShapes);
        w.WriteBoolean("groupNearShapes", o.GroupNearShapes);
        w.WriteNumber("shapeGroupGapPoints", o.ShapeGroupGapPoints);
        w.WriteBoolean("emphasis", o.Emphasis);
        w.WriteBoolean("usePdfText", o.UsePdfText);
        WriteNameOrNull(w, "extractor", document.Extractor); // the name only, not its command
        w.WriteString("folderLayout", Lower(o.FolderLayout));
        w.WriteString("markdownLanguage", document.MarkdownLanguage);
        w.WriteEndObject();
    }

    private static void WriteNames(Utf8JsonWriter w, string property, IEnumerable<string> names)
    {
        w.WriteStartArray(property);
        foreach (var name in names)
        {
            w.WriteStringValue(name);
        }

        w.WriteEndArray();
    }

    private static void WriteNameOrNull(Utf8JsonWriter w, string property, string? value)
    {
        if (value is null)
        {
            w.WriteNull(property);
        }
        else
        {
            w.WriteString(property, value);
        }
    }

    private static void WriteTime(Utf8JsonWriter w, string property, long utcTicks)
    {
        if (utcTicks <= 0)
        {
            w.WriteNull(property);
            return;
        }

        w.WriteString(property, new DateTime(utcTicks, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
    }

    /// <summary>The name of a value as <c>.markporto</c> keeps it: the name of the enum with a small first letter.</summary>
    private static string Lower<T>(T value) where T : struct, Enum
    {
        var text = value.ToString();
        return text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
    }
}
