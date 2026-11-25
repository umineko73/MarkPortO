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

using System.Text.Json;
using MarkPortO.Core.Engines;
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Markdown;
using MarkPortO.Core.Pipeline;
using MarkPortO.Core.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarkPortO.Core.Tests.Markdown;

/// <summary>FR-155: the record of the conversion of a document, <c>conversion.json</c> in its folder.</summary>
public class ConversionRecordTests
{
    private static JobState Job(string outputRoot, bool step3 = true) => new()
    {
        JobId = Guid.Parse("c4474252-01b6-481c-86a9-a417e06ef1fa"),
        CreatedAt = new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc),
        OutputRoot = outputRoot,
        Options = new OptionsSnapshot
        {
            Types = [DocumentKind.Docx],
            Dpi = 200,
            Engine = EngineKind.Office,
            Step2 = true,
            Step3 = step3,
            ExcelRange = ExcelRange.Sheet,
            FolderLayout = FolderLayout.Contents,
            PromptNames = new MarkPortO.Core.Settings.PromptNames { Docx = "図用" },
        },
        Step3Connection = step3 ? new Step3ConnectionInfo("command", @"C:\Tools\secret-reader.exe", "1.2", "gemma-4") : null,
    };

    private static DocumentRecord Document(string folder) => new()
    {
        DocId = "d0003",
        SourcePath = @"D:\設計書\v1.2\基本設計\設計書.docx",
        DisplayName = @"v1.2\基本設計\設計書.docx",
        Kind = DocumentKind.Docx,
        Fingerprint = new Fingerprint(123456, new DateTime(2026, 11, 20, 10, 15, 0, DateTimeKind.Utc).Ticks),
        OutputFolder = folder,
        MarkdownLanguage = "ja",
        Step1 = StepStatus.Done,
        Step2 = StepStatus.Done,
        Step3 = StepStatus.Done,
        Preset = "rag",
        Chunks = true,
        Step3Mode = Step3Mode.Supplement,
        VlmModel = "gemma-4",
        Pages =
        [
            new PageRecord { Index = 1, Step3 = PageStep3Status.Done },
            new PageRecord { Index = 2, Step3 = PageStep3Status.Failed },
            new PageRecord { Index = 3, Step3 = PageStep3Status.Skipped },
        ],
    };

    [Fact]
    public void TheRecord_HasTheInputTheStateAndTheConditions_InTheFormOfTheContract()
    {
        using var dir = new TempDir();
        var folder = dir.Combine("out", "設計書");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "設計書.md"), "#");
        File.WriteAllText(Path.Combine(folder, "設計書_chunks.jsonl"), "{}");
        File.WriteAllText(Path.Combine(folder, "設計書.log"), "");

        var text = ConversionRecord.Render(Job(dir.Combine("out")), Document(folder));

        Assert.DoesNotContain('\r', text); // LF
        Assert.Contains("設計書", text, StringComparison.Ordinal); // readable, not escaped
        using var json = JsonDocument.Parse(text);
        var r = json.RootElement;
        Assert.Equal(["schemaVersion", "app", "source", "outputRoot", "jobId", "docId", "startedAt", "updatedAt", "status", "steps", "pages", "error", "warnings", "files", "conditions"],
            r.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, r.GetProperty("schemaVersion").GetInt32());
        Assert.StartsWith("MarkPortO ", r.GetProperty("app").GetString());
        var source = r.GetProperty("source");
        Assert.Equal(@"D:\設計書\v1.2\基本設計\設計書.docx", source.GetProperty("path").GetString());
        Assert.Equal("v1.2/基本設計/設計書.docx", source.GetProperty("relativePath").GetString());
        Assert.Equal("docx", source.GetProperty("kind").GetString());
        Assert.Equal(123456, source.GetProperty("size").GetInt64());
        Assert.Equal("2026-11-20T10:15:00Z", source.GetProperty("lastWriteTime").GetString());
        Assert.Equal("c4474252-01b6-481c-86a9-a417e06ef1fa", r.GetProperty("jobId").GetString());
        Assert.Equal("completed", r.GetProperty("status").GetString());
        Assert.Equal("done", r.GetProperty("steps").GetProperty("step3").GetString());
        Assert.Equal(3, r.GetProperty("pages").GetProperty("total").GetInt32());
        Assert.Equal(1, r.GetProperty("pages").GetProperty("step3Done").GetInt32());
        Assert.Equal(1, r.GetProperty("pages").GetProperty("step3Failed").GetInt32());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("error").ValueKind);
        var files = r.GetProperty("files");
        Assert.Equal(["設計書.md"], files.GetProperty("markdown").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(files.GetProperty("vlm").EnumerateArray()); // not written: not listed
        Assert.Equal(["設計書_chunks.jsonl"], files.GetProperty("chunks").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("設計書.log", files.GetProperty("log").GetString());
        Assert.Equal(JsonValueKind.Null, files.GetProperty("sheets").ValueKind);
        var c = r.GetProperty("conditions");
        Assert.Equal("rag", c.GetProperty("preset").GetString());
        Assert.Equal("office", c.GetProperty("engine").GetString());
        Assert.Equal(200, c.GetProperty("dpi").GetInt32());
        Assert.Equal("supplement", c.GetProperty("step3Mode").GetString());
        Assert.Equal("sheet", c.GetProperty("excelRange").GetString());
        Assert.Equal("contents", c.GetProperty("folderLayout").GetString());
        Assert.Equal("図用", c.GetProperty("promptNames").GetProperty("docx").GetString());
        Assert.Equal("command", c.GetProperty("step3Connection").GetProperty("kind").GetString());
        Assert.Equal("gemma-4", c.GetProperty("step3Connection").GetProperty("model").GetString());
    }

    [Fact]
    public void TheRecord_NeverHasTheCommandOfTheConnection()
    {
        using var dir = new TempDir();
        var text = ConversionRecord.Render(Job(dir.Path), Document(dir.Path));

        Assert.DoesNotContain("secret-reader", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\\Tools", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(StepStatus.Done, StepStatus.Running, "running")]
    [InlineData(StepStatus.Done, StepStatus.Interrupted, "interrupted")]
    [InlineData(StepStatus.Done, StepStatus.Failed, "failed")]
    [InlineData(StepStatus.Done, StepStatus.NotApplicable, "completed")]
    public void TheStatus_FollowsTheSteps(StepStatus step2, StepStatus step3, string expected)
    {
        var document = Document("x");
        document.Step2 = step2;
        document.Step3 = step3;
        if (step3 == StepStatus.Failed)
        {
            document.Error = new DocumentError(3, "timeout");
        }

        Assert.Equal(expected, ConversionRecord.Status(document));
    }

    [Fact]
    public void Step3NotUsed_IsWrittenAsNotApplicable_WithoutAConnection()
    {
        using var dir = new TempDir();
        var document = Document(dir.Path);
        document.Step3 = StepStatus.NotApplicable;

        using var json = JsonDocument.Parse(ConversionRecord.Render(Job(dir.Path, step3: false), document));

        Assert.Equal("notApplicable", json.RootElement.GetProperty("steps").GetProperty("step3").GetString());
        Assert.False(json.RootElement.GetProperty("conditions").GetProperty("step3").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("conditions").GetProperty("step3Connection").ValueKind);
    }

    [Fact]
    public void ARecordThatCannotBeWritten_IsAWarning_NotAFailure()
    {
        using var dir = new TempDir();
        var blocked = dir.Combine("blocked");
        File.WriteAllText(blocked, "a file where the folder should be");
        var document = Document(Path.Combine(blocked, "設計書"));

        ConversionRecord.Write(Job(dir.Path), document);

        Assert.Single(document.Warnings, w => w.Code == ConversionRecord.FailedWarning);
    }

    [Fact]
    public void TheContextOfAJob_WritesTheRecord_WhenAStepChanges_NotAtEveryPage()
    {
        using var dir = new TempDir();
        var folder = dir.Combine("out", "設計書");
        var job = Job(dir.Combine("out"));
        var store = new StateStore(dir.Combine("out"));
        store.SaveJob(job);
        var context = new JobContext(job, store, new NullEngine(), NullLogger.Instance, TimeSpan.FromSeconds(1));
        var document = Document(folder);
        document.Step3 = StepStatus.Running;
        var record = Path.Combine(folder, ConversionRecord.FileName);

        context.Save(document);
        Assert.True(File.Exists(record));
        Assert.Contains("\"running\"", File.ReadAllText(record), StringComparison.Ordinal);

        File.Delete(record);
        document.Pages[0].Step3 = PageStep3Status.Running; // a page read: the steps are the same
        context.Save(document);
        Assert.False(File.Exists(record));

        document.Step3 = StepStatus.Interrupted;
        context.Save(document);
        Assert.Contains("\"interrupted\"", File.ReadAllText(record), StringComparison.Ordinal);
    }

    private sealed class NullEngine : IDocumentEngine
    {
        public EngineKind Kind => EngineKind.Office;

        public EngineAvailability CheckAvailability() => EngineAvailability.Unavailable("not used");

        public Task ExportPdfAsync(PdfExportRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
