// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
// Author: umineko73
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

using MarkPort.Core.Convert;
using MarkPort.Core.Jobs;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Pipeline;
using MarkPort.Core.Settings;
using MarkPort.Core.Tests.Fixtures;
using MarkPort.Core.Vlm;
using static MarkPort.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPort.Core.Tests.Pipeline;

/// <summary>Step 3, the VLM reading of the pages (FR-008, FR-021, FR-022, SC-006, SC-014), against a fake OpenAI server.</summary>
[Trait("Engine", "LibreOffice")]
public class Step3Tests
{
    private static AppSettings Settings(FakeOpenAiServer server, string? key = null, int timeoutSeconds = 30)
    {
        var settings = AppSettings.CreateDefault();
        settings.Vlm.Connections.Add(new ConnectionProfile { Name = "fake", BaseUrl = server.BaseUrl, Model = "vision-model", ApiKey = key, TimeoutSeconds = timeoutSeconds });
        settings.Vlm.Selected = "fake";
        settings.Vlm.Prompts = new PromptSettings { Docx = "DOCX-PROMPT", Xlsx = "XLSX-PROMPT", Pptx = "PPTX-PROMPT" };
        return settings;
    }

    private static async Task<(ConvertResult Result, ConvertService Service)> RunWithVlmAsync(TempDir dir, FakeOpenAiServer server, ConvertRequest request, string? key = null, int timeoutSeconds = 30)
    {
        var (setup, error) = VlmSetupFactory.Create(Settings(server, key, timeoutSeconds));
        Assert.True(setup is not null, error);
        using var engine = Engine(dir);
        var service = new ConvertService(engine, vlm: setup);
        var result = await service.RunAsync(request);
        setup!.Dispose();
        return (result, service);
    }

    private static string MarkdownOf(DocumentRecord document) =>
        File.ReadAllText(Path.Combine(document.OutputFolder, Path.GetFileNameWithoutExtension(document.SourcePath) + ".md"));

    [LibreOfficeFact]
    public async Task EveryPageIsRead_AndTheResultAndUsageAreInTheMarkdown()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { PageReply = p => "ECHO:" + p, PromptTokens = 100, CompletionTokens = 20 };
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["page one text"], ["page two text"]);

        var (result, _) = await RunWithVlmAsync(dir, server, Request(dir.Combine("out"), Options(step3: true, connection: "fake"), source));

        var document = Assert.Single(result.Documents);
        Assert.True(document.IsDone());
        Assert.Equal(StepStatus.Done, document.Step3);
        Assert.Equal(2, server.Requests.Count(r => r.HasImage));
        var markdown = MarkdownOf(document);
        Assert.Contains("<!-- vlm:begin page=1 -->\nECHO:DOCX-PROMPT\n<!-- vlm:end -->\n<!-- usage page=1 prompt=100 completion=20 total=120 -->", markdown);
        Assert.Contains("<!-- vlm:begin page=2 -->", markdown);
        Assert.Contains("> **VLM使用量**: 合計 prompt=200 completion=40 total=240（ページ数: 2、取得できたページ: 2）", markdown);
        // SC-006: the pages add up to the file total.
        Assert.Equal(240, document.UsageTotal!.TotalTokens);
        Assert.Equal(240, document.Pages.Sum(p => p.Usage!.TotalTokens));
    }

    [LibreOfficeFact]
    public async Task EachDocumentType_UsesItsOwnPrompt_FR021()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { PageReply = p => "ECHO:" + p };
        var docx = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(docx, ["d"]);
        var pptx = dir.Combine("in", "b.pptx");
        PptxFactory.Create(pptx, new SlideSpec("s", ["x"]));
        var xlsx = dir.Combine("in", "c.xlsx");
        XlsxFactory.Create(xlsx, new SheetSpec("S", [["v"]]));

        var (result, _) = await RunWithVlmAsync(dir, server, Request(dir.Combine("out"), Options(step3: true, connection: "fake"), docx, pptx, xlsx));

        Assert.Contains("ECHO:DOCX-PROMPT", MarkdownOf(result.Documents.Single(d => d.Kind == DocumentKind.Docx)));
        Assert.Contains("ECHO:PPTX-PROMPT", MarkdownOf(result.Documents.Single(d => d.Kind == DocumentKind.Pptx)));
        Assert.Contains("ECHO:XLSX-PROMPT", MarkdownOf(result.Documents.Single(d => d.Kind == DocumentKind.Xlsx)));
    }

    [LibreOfficeFact]
    public async Task WithStep3Off_NoConnectionToTheAiIsMade_SC014()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["text"]);

        // Even with a connection configured and a server running, nothing is sent when step 3 is off.
        var (setup, _) = VlmSetupFactory.Create(Settings(server));
        using var engine = Engine(dir);
        var result = await new ConvertService(engine, vlm: setup).RunAsync(Request(dir.Combine("out"), Options(step3: false), source));

        Assert.True(result.Documents[0].IsDone());
        Assert.Empty(server.Requests);
        Assert.DoesNotContain("vlm:begin", MarkdownOf(result.Documents[0]));
        setup!.Dispose();
    }

    [LibreOfficeFact]
    public async Task ADeletedPage_IsNeverSentToTheAi()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreateWithShapes(source, [new DocPage(["kept"]), new DocPage(["deleted"], DocShape.CrossShape())]);

        var (result, _) = await RunWithVlmAsync(dir, server, Request(dir.Combine("out"), Options(step3: true, connection: "fake"), source));

        Assert.Equal(1, server.Requests.Count(r => r.HasImage));
        var document = result.Documents[0];
        Assert.Equal(PageStep3Status.Skipped, document.Pages[1].Step3);
        Assert.DoesNotContain("vlm:begin page=2", MarkdownOf(document));
    }

    [LibreOfficeFact]
    public async Task AFailedPage_IsRecorded_AndTheOtherPagesAreStillRead_FR029()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { FailChatRequest = n => n == 2 }; // the second page fails
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"], ["two"], ["three"]);

        var (result, _) = await RunWithVlmAsync(dir, server, Request(dir.Combine("out"), Options(step3: true, connection: "fake"), source));

        var document = Assert.Single(result.Documents);
        Assert.True(document.IsDone()); // the file went on
        Assert.Equal([PageStep3Status.Done, PageStep3Status.Failed, PageStep3Status.Done], document.Pages.Select(p => p.Step3));
        Assert.True(document.HasFailedPages());
        Assert.True(result.HasFailures);
        var markdown = MarkdownOf(document);
        Assert.Contains("<!-- vlm:failed page=2 reason=", markdown);
        Assert.Contains("<!-- vlm:begin page=3 -->", markdown);
        Assert.Contains("VlmPageFailed", document.Warnings.Select(w => w.Code));
        // The total counts only the pages that were read.
        Assert.Equal(240, document.UsageTotal!.TotalTokens);
    }

    [LibreOfficeFact]
    public async Task APageThatTimesOut_IsRecordedAsFailed_AndTheFileStillCompletes()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { ChatDelay = TimeSpan.FromSeconds(4) };
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"]);

        var (result, _) = await RunWithVlmAsync(dir, server, Request(dir.Combine("out"), Options(step3: true, connection: "fake"), source), timeoutSeconds: 1);

        var document = Assert.Single(result.Documents);
        Assert.True(document.IsDone());
        Assert.Equal(PageStep3Status.Failed, document.Pages[0].Step3);
        Assert.Contains("1 seconds", document.Pages[0].Error);
    }

    private static int ChatRequests(FakeOpenAiServer server)
    {
        lock (server.Requests)
        {
            return server.Requests.Count(r => r.Path.EndsWith("chat/completions", StringComparison.Ordinal));
        }
    }

    [LibreOfficeFact]
    public async Task APageThatTimesOutOnce_IsReadOnTheSecondTry_FR013c()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { StallChatRequest = n => n == 1 };
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"]);

        var (result, _) = await RunWithVlmAsync(dir, server, Request(dir.Combine("out"), Options(step3: true, connection: "fake"), source), timeoutSeconds: 1);

        var document = Assert.Single(result.Documents);
        Assert.Equal(PageStep3Status.Done, document.Pages[0].Step3);
        Assert.False(document.HasFailedPages());
        Assert.Equal(2, ChatRequests(server)); // the stalled one and the retry
    }

    [LibreOfficeFact]
    public async Task APageThatTimesOutTwice_IsFailed_AndTheFileShowsPartlyFailed_FR013b()
    {
        using var japanese = Localization.TestCulture.Use("ja");
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { StallChatRequest = n => n is 2 or 3 }; // both tries of page 2
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"], ["two"], ["three"]);

        var (result, _) = await RunWithVlmAsync(dir, server, Request(dir.Combine("out"), Options(step3: true, connection: "fake"), source), timeoutSeconds: 1);

        var document = Assert.Single(result.Documents);
        Assert.Equal([PageStep3Status.Done, PageStep3Status.Failed, PageStep3Status.Done], document.Pages.Select(p => p.Step3));
        var row = FileStatusProjector.Project(document);
        Assert.Equal(FileDisplayState.DoneWithPageFailures, row.State);
        Assert.Equal("完了（一部失敗）", FileStatusProjector.Text(row.State));
        Assert.Contains("1ページ", row.Message);
        Assert.Contains("1 秒", row.Message);
        Assert.Equal(JobStatus.CompletedWithErrors, result.Job.Status);
    }

    [LibreOfficeFact]
    public async Task RetryFailed_ReadsOnlyTheFailedPages_OfAFinishedFile_FR013b()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { StallChatRequest = n => n is 2 or 3 };
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"], ["two"], ["three"]);
        var outFolder = dir.Combine("out");
        var (setup, error) = VlmSetupFactory.Create(Settings(server, timeoutSeconds: 1));
        Assert.True(setup is not null, error);
        using var engine = Engine(dir);
        await new ConvertService(engine, vlm: setup).RunAsync(Request(outFolder, Options(step3: true, connection: "fake"), source));
        var before = ChatRequests(server);

        var retried = await new ConvertService(engine, vlm: setup).ResumeAsync(
            new ResumeRequest { OutputFolder = outFolder, Mode = ResumeMode.RetryFailed, Step3 = true, Connection = "fake" });
        setup!.Dispose();

        var document = Assert.Single(retried.Documents);
        Assert.Equal(4, before); // page 1, page 2 twice, page 3
        Assert.Equal(1, ChatRequests(server) - before); // only the failed page was read again
        Assert.Equal([PageStep3Status.Done, PageStep3Status.Done, PageStep3Status.Done], document.Pages.Select(p => p.Step3));
        Assert.Equal(FileDisplayState.Done, FileStatusProjector.Project(document).State);
        var markdown = MarkdownOf(document);
        Assert.DoesNotContain("vlm:failed", markdown);
        Assert.Contains("<!-- vlm:begin page=2 -->", markdown);
        Assert.DoesNotContain("VlmPageFailed", document.Warnings.Select(w => w.Code));
    }

    [LibreOfficeFact]
    public async Task WhenTheServerGivesNoUsage_TheMarkdownSaysItIsUnavailable_AndNothingFails()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { ReportsUsage = false };
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"]);

        var (result, _) = await RunWithVlmAsync(dir, server, Request(dir.Combine("out"), Options(step3: true, connection: "fake"), source));

        var markdown = MarkdownOf(result.Documents[0]);
        Assert.Contains("<!-- usage page=1 unavailable -->", markdown);
        Assert.Contains("> **VLM使用量**: **取得不可**", markdown);
        Assert.False(result.HasFailures);
    }

    [LibreOfficeFact]
    public async Task TheApiKey_IsInNoOutputAndNoStateFile_SC012()
    {
        using var dir = new TempDir();
        const string key = "sk-very-secret-key-424242";
        using var server = new FakeOpenAiServer { RequiredKey = key };
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"], ["two"]);
        var outFolder = dir.Combine("out");

        var (result, _) = await RunWithVlmAsync(dir, server, Request(outFolder, Options(step3: true, connection: "fake"), source), key: key);

        Assert.True(result.Documents[0].IsDone());
        var leaks = Directory.EnumerateFiles(outFolder, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".png", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(key, StringComparison.Ordinal))
            .ToList();
        Assert.Empty(leaks);
        Assert.DoesNotContain(key, ConvertResultJson.Serialize(result));
    }

    [LibreOfficeFact]
    public async Task AResume_ContinuesWithTheNextPage_NotFromTheFirst()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"], ["two"], ["three"], ["four"]);
        var outFolder = dir.Combine("out");
        var (setup, _) = VlmSetupFactory.Create(Settings(server));
        using var engine = Engine(dir);
        using var cts = new CancellationTokenSource();
        var service = new ConvertService(engine, vlm: setup);
        service.DocumentChanged += d =>
        {
            // as soon as the first page has been read
            if (d.Step3 == StepStatus.Running && d.Pages.Count(p => p.Step3 == PageStep3Status.Done) == 1)
            {
                cts.Cancel();
            }
        };

        var interrupted = await service.RunAsync(Request(outFolder, Options(step3: true, connection: "fake"), source), cts.Token);
        Assert.Equal(JobStatus.Interrupted, interrupted.Job.Status);
        var readBefore = server.Requests.Count(r => r.HasImage);
        Assert.InRange(readBefore, 1, 3);

        var resumed = await new ConvertService(engine, vlm: setup).ResumeAsync(new ResumeRequest { OutputFolder = outFolder });

        Assert.Equal(JobStatus.Completed, resumed.Job.Status);
        Assert.Equal(4, resumed.Documents[0].Pages.Count(p => p.Step3 == PageStep3Status.Done));
        // Each page was read once in total (a page being read when the run stopped may be read again).
        Assert.InRange(server.Requests.Count(r => r.HasImage), 4, 5);
        setup!.Dispose();
    }

    [LibreOfficeFact]
    public async Task TurningStep3On_OnResume_ReadsTheAlreadyConvertedFiles_WithoutConvertingThemAgain()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer();
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"], ["two"]);
        var outFolder = dir.Combine("out");
        var first = await RunAsync(dir, Request(outFolder, Options(), source)); // no step 3
        var imageTime = File.GetLastWriteTimeUtc(Path.Combine(first.Documents[0].OutputFolder, "images", "page-001.png"));
        var store = new StateStore(outFolder);
        var job = store.LoadJob(first.Job.JobId)!;
        job.Status = JobStatus.Interrupted;
        store.SaveJob(job, touch: false);

        var (setup, _) = VlmSetupFactory.Create(Settings(server));
        using var engine = Engine(dir);
        var resumed = await new ConvertService(engine, vlm: setup).ResumeAsync(new ResumeRequest { OutputFolder = outFolder, Step3 = true });

        Assert.Equal(StepStatus.Done, resumed.Documents[0].Step3);
        Assert.Equal(2, server.Requests.Count(r => r.HasImage));
        Assert.Equal(imageTime, File.GetLastWriteTimeUtc(Path.Combine(resumed.Documents[0].OutputFolder, "images", "page-001.png")));
        Assert.Contains("<!-- vlm:begin page=2 -->", MarkdownOf(resumed.Documents[0]));
        setup!.Dispose();
    }
}
