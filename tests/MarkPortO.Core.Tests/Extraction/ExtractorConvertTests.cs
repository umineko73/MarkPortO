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
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;
using PdfPig = UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using static MarkPortO.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPortO.Core.Tests.Extraction;

/// <summary>FR-111 and FR-112 (experimental): a document whose Markdown an extractor writes.</summary>
public class ExtractorConvertTests
{
    private static readonly string Cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    private static ExtractorProfile Profile(params string[] args) => new() { Name = "t", Command = Cmd, Args = [.. args], Output = ExtractorOutput.File, TimeoutSeconds = 30 };

    private static OptionsSnapshot PdfOptions(string extractor)
    {
        var options = Options();
        options.Types = [DocumentKind.Pdf];
        options.Extractors.Pdf = extractor;
        return options;
    }

    private static async Task<ConvertResult> RunWithAsync(TempDir dir, ConvertRequest request, params ExtractorProfile[] extractors)
    {
        using var engine = Engine(dir);
        return await new ConvertService(engine, extractors: extractors).RunAsync(request);
    }

    [Fact]
    public async Task TheMarkdownIsTheOneOfTheExtractor_WithoutPageMarks_AndThePageImagesAreStillSaved()
    {
        using var dir = new TempDir();
        var source = PdfFactory.Create(dir.Combine("in", "a.pdf"), PdfPig.Content.PageSize.A4, "First page", "Second page");

        var result = await RunWithAsync(dir, Request(dir.Combine("out"), PdfOptions("t"), source), Profile("/c", "echo", "#", "From", "the", "extractor", ">", "{output}", "&", "rem", "{input}"));

        var document = Assert.Single(result.Documents);
        Assert.True(document.IsDone());
        Assert.Equal("t", document.Extractor);
        Assert.Equal(2, ImageFiles(document.OutputFolder).Length);
        var markdown = File.ReadAllText(Path.Combine(document.OutputFolder, "a.md"));
        Assert.Contains("# From the extractor", markdown);
        Assert.Contains("`t`", markdown);
        Assert.DoesNotContain("<!-- page:", markdown);
        Assert.DoesNotContain("![", markdown);
        Assert.DoesNotContain("First page", markdown);
    }

    [Fact]
    public async Task AnExtractorThatFails_FailsThatDocument_AndTheOtherDocumentsAreConverted()
    {
        using var dir = new TempDir();
        var good = PdfFactory.Create(dir.Combine("in", "good.pdf"), PdfPig.Content.PageSize.A4, "Text");
        var other = PdfFactory.Create(dir.Combine("in", "other.docx.pdf"), PdfPig.Content.PageSize.A4, "Text");
        var options = PdfOptions("t");

        // The command fails for a file whose name has "other" in it.
        var profile = Profile("/c", "echo", "{input}", "|", "findstr", "/v", "other", ">", "{output}");
        var result = await RunWithAsync(dir, Request(dir.Combine("out"), options, good, other), profile);

        Assert.Equal(1, result.Documents.Count(d => d.IsDone()));
        Assert.Equal(1, result.Documents.Count(d => d.IsFailed()));
    }

    [Fact]
    public async Task AnExtractorThatIsNotInTheSettings_StopsTheRunBeforeItStarts()
    {
        using var dir = new TempDir();
        var source = PdfFactory.Create(dir.Combine("in", "a.pdf"), PdfPig.Content.PageSize.A4, "Text");

        var ex = await Assert.ThrowsAsync<ConvertStartException>(() => RunWithAsync(dir, Request(dir.Combine("out"), PdfOptions("missing"), source)));

        Assert.Equal(ConvertStartException.ExtractorItem, ex.Item);
    }

    [Fact]
    public async Task AnExtractorThatSendsDocumentsOutside_NeedsTheConsent()
    {
        using var dir = new TempDir();
        var source = PdfFactory.Create(dir.Combine("in", "a.pdf"), PdfPig.Content.PageSize.A4, "Text");
        var profile = Profile("/c", "echo", "x", ">", "{output}", "&", "rem", "{input}");
        profile.SendsExternally = true;

        var ex = await Assert.ThrowsAsync<ConvertStartException>(() => RunWithAsync(dir, Request(dir.Combine("out"), PdfOptions("t"), source), profile));

        Assert.Equal(ConvertStartException.ExtractorItem, ex.Item);
    }

    [Fact]
    public async Task TheBuiltInExtractionIsUsed_WhenNoExtractorIsChosen()
    {
        using var dir = new TempDir();
        var source = PdfFactory.Create(dir.Combine("in", "a.pdf"), PdfPig.Content.PageSize.A4, "Built in text");

        var result = await RunWithAsync(dir, Request(dir.Combine("out"), PdfOptions(string.Empty), source), Profile("/c", "echo", "x", ">", "{output}", "&", "rem", "{input}"));

        var document = Assert.Single(result.Documents);
        Assert.Null(document.Extractor);
        Assert.Contains("Built in text", File.ReadAllText(Path.Combine(document.OutputFolder, "a.md")));
    }
}
