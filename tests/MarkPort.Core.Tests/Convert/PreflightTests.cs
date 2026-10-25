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

using MarkPort.Core.Convert;
using MarkPort.Core.Jobs;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Settings;
using MarkPort.Core.Tests.Fixtures;
using MarkPort.Core.Tests.Localization;
using MarkPort.Core.Vlm;
using static MarkPort.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPort.Core.Tests.Convert;

/// <summary>FR-019c: with step 3 on, the VLM connection is checked before step 1 starts, on a new run and on a resume.</summary>
public class PreflightTests : JapaneseUiTests
{
    private static AppSettings Settings(string baseUrl, string model = "vision-model", string? key = null)
    {
        var settings = AppSettings.CreateDefault();
        settings.Vlm.Connections.Add(new ConnectionProfile { Name = "fake", BaseUrl = baseUrl, Model = model, ApiKey = key, TimeoutSeconds = 5 });
        settings.Vlm.Selected = "fake";
        return settings;
    }

    private static string MakeDocx(TempDir dir)
    {
        var source = dir.Combine("in", "a.docx");
        DocxFactory.CreatePaged(source, ["one"]);
        return source;
    }

    [LibreOfficeFact]
    [Trait("Engine", "LibreOffice")]
    public async Task AWorkingConnection_IsCheckedFirst_AndTheRunProceeds()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer();
        var source = MakeDocx(dir);
        var (setup, error) = VlmSetupFactory.Create(Settings(server.BaseUrl));
        Assert.True(setup is not null, error);
        using var engine = Engine(dir);

        var result = await new ConvertService(engine, vlm: setup).RunAsync(Request(dir.Combine("out"), Options(step3: true, connection: "fake"), source));

        Assert.True(result.Documents[0].IsDone());
        // The very first request is the connection check (the model list), before any page is sent.
        Assert.Equal("/v1/models", server.Requests[0].Path);
        Assert.False(server.Requests[0].HasImage);
        setup!.Dispose();
    }

    [Fact]
    public async Task AConnectionThatFails_StopsBeforeStep1_AndWritesNothing()
    {
        using var dir = new TempDir();
        var source = MakeDocx(dir);
        var outFolder = dir.Combine("out");
        var (setup, _) = VlmSetupFactory.Create(Settings("http://localhost:1/v1")); // nothing listens there
        using var engine = Engine(dir);

        var error = await Assert.ThrowsAsync<ConvertStartException>(() =>
            new ConvertService(engine, vlm: setup).RunAsync(Request(outFolder, Options(step3: true, connection: "fake"), source)));

        Assert.Contains("接続チェックに失敗", error.Message);
        Assert.Contains("到達できません", error.Message);
        Assert.False(Directory.Exists(outFolder)); // nothing was started or written
        setup!.Dispose();
    }

    [Fact]
    public async Task AMissingModel_AndARefusedKey_AreReportedWithTheirReason()
    {
        using var dir = new TempDir();
        var source = MakeDocx(dir);
        using var strict = new FakeOpenAiServer { RequiredKey = "the-right-key-0001", Models = ["alpha"] };
        using var engine = Engine(dir);

        var (noModel, _) = VlmSetupFactory.Create(Settings(strict.BaseUrl, model: "missing-model", key: "the-right-key-0001"));
        var modelError = await Assert.ThrowsAsync<ConvertStartException>(() =>
            new ConvertService(engine, vlm: noModel).RunAsync(Request(dir.Combine("o1"), Options(step3: true), source)));
        Assert.Contains("missing-model", modelError.Message);

        var (wrongKey, _) = VlmSetupFactory.Create(Settings(strict.BaseUrl, key: "a-wrong-key-99999"));
        var keyError = await Assert.ThrowsAsync<ConvertStartException>(() =>
            new ConvertService(engine, vlm: wrongKey).RunAsync(Request(dir.Combine("o2"), Options(step3: true), source)));
        Assert.Contains("認証", keyError.Message);

        Assert.False(Directory.Exists(dir.Combine("o1")));
        Assert.False(Directory.Exists(dir.Combine("o2")));
        noModel!.Dispose();
        wrongKey!.Dispose();
    }

    [Fact]
    public async Task ThePreflight_NeverRunsTheImageCheck()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer { SupportsImages = false }; // a text-only model
        var source = MakeDocx(dir);
        var (setup, _) = VlmSetupFactory.Create(Settings(server.BaseUrl));
        using var engine = Engine(dir);

        // The connection check passes; the run is not stopped by the image check, which the user runs by hand.
        var preflight = await setup!.Preflight(CancellationToken.None);

        Assert.True(preflight.Ok);
        Assert.DoesNotContain(server.Requests, r => r.HasImage);
        setup.Dispose();
    }

    [Fact]
    public async Task WithStep3OnButNoConnection_TheRunDoesNotStart()
    {
        using var dir = new TempDir();
        var source = MakeDocx(dir);
        using var engine = Engine(dir);

        var error = await Assert.ThrowsAsync<ConvertStartException>(() =>
            new ConvertService(engine).RunAsync(Request(dir.Combine("out"), Options(step3: true), source)));

        Assert.Contains("接続が設定されていません", error.Message);
        Assert.False(Directory.Exists(dir.Combine("out")));
    }

    [Fact]
    public void WithoutASelectedConnection_TheSetupCannotBeMade()
    {
        var settings = AppSettings.CreateDefault();

        var (setup, error) = VlmSetupFactory.Create(settings);

        Assert.Null(setup);
        Assert.Contains("選ばれていません", error);
    }

    [Fact]
    public void AnEnvironmentKeyThatIsNotSet_StopsTheSetup_WithTheVariableName()
    {
        var settings = Settings("http://localhost:1234/v1");
        settings.Vlm.Connections[0].ApiKeyEnv = "MARKPORT_TEST_UNSET_KEY";

        var (setup, error) = VlmSetupFactory.Create(settings, _ => null);

        Assert.Null(setup);
        Assert.Contains("MARKPORT_TEST_UNSET_KEY", error);
    }

    [Fact]
    public async Task AResume_ChecksTheConnectionToo_BeforeAnyStep()
    {
        using var dir = new TempDir();
        var source = MakeDocx(dir);
        var outFolder = dir.Combine("out");
        var store = new StateStore(outFolder);
        var (job, documents) = ConvertService.Plan(Request(outFolder, Options(step3: true, connection: "fake"), source), store);
        job.Status = JobStatus.Interrupted;
        documents[0].Step1 = StepStatus.Interrupted;
        store.SaveJob(job);
        store.SaveDocument(job.JobId, documents[0]);
        var (setup, _) = VlmSetupFactory.Create(Settings("http://localhost:1/v1"));
        using var engine = Engine(dir);

        var error = await Assert.ThrowsAsync<ConvertStartException>(() =>
            new ConvertService(engine, vlm: setup).ResumeAsync(new ResumeRequest { OutputFolder = outFolder }));

        Assert.Contains("接続チェックに失敗", error.Message);
        Assert.False(File.Exists(Path.Combine(documents[0].OutputFolder, "a.md"))); // nothing ran
        setup!.Dispose();
    }

    [Fact]
    public async Task WithStep3Off_NoCheckIsDone()
    {
        using var dir = new TempDir();
        using var server = new FakeOpenAiServer();
        var source = MakeDocx(dir);
        var (setup, _) = VlmSetupFactory.Create(Settings(server.BaseUrl));
        using var engine = Engine(dir);

        // step 3 off: the engine here is not installed in this test, so the run stops for another reason - but not the VLM.
        try
        {
            await new ConvertService(engine, vlm: setup).RunAsync(Request(dir.Combine("out"), Options(step3: false), source));
        }
        catch (ConvertStartException)
        {
            // LibreOffice may be missing on this machine; only the absence of VLM traffic matters here.
        }

        Assert.Empty(server.Requests);
        setup!.Dispose();
    }
}
