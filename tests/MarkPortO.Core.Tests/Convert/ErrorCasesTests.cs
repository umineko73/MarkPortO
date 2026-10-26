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

using MarkPortO.Core.Convert;
using MarkPortO.Core.Engines;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Tests.Fixtures;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Convert;

[Trait("Engine", "LibreOffice")]
public class ErrorCasesTests
{
    [LibreOfficeFact]
    public async Task ADamagedFile_IsReportedAsFailed_AndTheOtherFilesStillConvert()
    {
        using var dir = new TempDir();
        var broken = dir.Combine("in", "a-broken.docx");
        var good = dir.Combine("in", "b-good.docx");
        DocxFactory.CreateCorrupt(broken);
        DocxFactory.CreatePaged(good, ["good text"]);

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), broken, good));

        var failed = result.Documents.Single(d => d.SourcePath == broken);
        var ok = result.Documents.Single(d => d.SourcePath == good);
        Assert.Equal(StepStatus.Failed, failed.Step1);
        Assert.NotNull(failed.Error);
        Assert.Equal(1, failed.Error!.Step);
        Assert.Contains("cannot be opened", failed.Error.Message);
        Assert.True(ok.IsDone()); // SC-007
        Assert.Equal(JobStatus.CompletedWithErrors, result.Job.Status);
        Assert.Contains("good text", File.ReadAllText(Path.Combine(ok.OutputFolder, "b-good.md")));
        // The damaged file produced no half-finished output (LibreOffice would have "converted" it as text).
        Assert.Empty(ImageFiles(failed.OutputFolder));
        Assert.False(File.Exists(Path.Combine(failed.OutputFolder, "a-broken.md")));
    }

    [LibreOfficeFact]
    public async Task APasswordProtectedLookingFile_IsFailedWithAHint_WithoutShowingADialog()
    {
        using var dir = new TempDir();
        var protectedFile = dir.Combine("in", "secret.docx");
        Directory.CreateDirectory(dir.Combine("in"));
        // Encrypted Office files are OLE compound files, which start with this signature.
        File.WriteAllBytes(protectedFile, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), protectedFile));

        var document = Assert.Single(result.Documents);
        Assert.Equal(StepStatus.Failed, document.Step1);
        Assert.Contains("password", document.Error!.Message);
    }

    [LibreOfficeFact]
    public async Task APresentationWithoutSlides_CompletesWithAWarning_AndOnlyTheHeaderInTheMarkdown()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "empty.pptx");
        PptxFactory.Create(source);

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        var document = Assert.Single(result.Documents);
        Assert.True(document.IsDone());
        Assert.Empty(document.Pages);
        Assert.Contains("EmptyDocument", document.Warnings.Select(w => w.Code));
        Assert.Empty(ImageFiles(document.OutputFolder));
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "empty.md"));
        Assert.StartsWith("# empty.pptx", markdown);
        Assert.DoesNotContain("\n<!-- page:", markdown);
    }

    [LibreOfficeFact]
    public async Task AWorkbookWhoseSheetsAreAllHidden_CompletesWithAWarning_AndNoImages()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "hidden.xlsx");
        XlsxFactory.Create(source, new SheetSpec("Secret", [["s"]], Hidden: true));

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), source));

        var document = Assert.Single(result.Documents);
        Assert.True(document.IsDone());
        Assert.Empty(document.Pages);
        Assert.Contains("EmptyDocument", document.Warnings.Select(w => w.Code));
        Assert.Empty(ImageFiles(document.OutputFolder));
    }

    [LibreOfficeFact]
    public async Task AFileThatDisappearedAfterPlanning_IsFailedAndTheRestContinues()
    {
        using var dir = new TempDir();
        var present = dir.Combine("in", "present.docx");
        DocxFactory.CreatePaged(present, ["still here"]);
        var missing = dir.Combine("in", "missing.docx");

        var result = await RunAsync(dir, Request(dir.Combine("out"), Options(), missing, present));

        var failed = result.Documents.Single(d => d.SourcePath == missing);
        Assert.Equal(StepStatus.Failed, failed.Step1);
        Assert.Contains("not found", failed.Error!.Message);
        Assert.True(result.Documents.Single(d => d.SourcePath == present).IsDone());
    }

    [Fact]
    public async Task AnUnsupportedFileType_StopsBeforeAnythingIsWritten()
    {
        using var dir = new TempDir();
        var text = dir.Combine("in", "notes.txt");
        Directory.CreateDirectory(dir.Combine("in"));
        File.WriteAllText(text, "x");
        var outFolder = dir.Combine("out");

        var error = await Assert.ThrowsAsync<ConvertStartException>(() => RunAsync(dir, Request(outFolder, Options(), text)));

        Assert.Contains("notes.txt", error.Message);
        Assert.False(Directory.Exists(outFolder));
    }

    [Fact]
    public async Task NoDocuments_StopsBeforeAnythingIsWritten()
    {
        using var dir = new TempDir();
        var outFolder = dir.Combine("out");

        await Assert.ThrowsAsync<ConvertStartException>(() => RunAsync(dir, Request(outFolder, Options())));

        Assert.False(Directory.Exists(outFolder));
    }

    [Fact]
    public async Task AnEngineThatIsNotAvailable_StopsBeforeAnythingIsWritten_WithTheReason()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["x"]);
        var outFolder = dir.Combine("out");
        using var engine = new LibreOfficeEngine(@"C:\no\such\soffice.exe", profileRoot: dir.Combine("profiles"));
        var service = new ConvertService(engine);

        var error = await Assert.ThrowsAsync<ConvertStartException>(() =>
            service.RunAsync(Request(outFolder, Options(), source)));

        Assert.Contains("C:\\no\\such\\soffice.exe", error.Message);
        Assert.False(Directory.Exists(outFolder));
    }

    [LibreOfficeFact]
    public async Task TheSourceOfAFailedFile_IsNotChanged()
    {
        using var dir = new TempDir();
        var broken = dir.Combine("in", "broken.docx");
        DocxFactory.CreateCorrupt(broken);
        var bytes = File.ReadAllBytes(broken);

        await RunAsync(dir, Request(dir.Combine("out"), Options(), broken));

        Assert.Equal(bytes, File.ReadAllBytes(broken));
    }
}
