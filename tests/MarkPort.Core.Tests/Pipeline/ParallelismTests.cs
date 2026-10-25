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

using System.Diagnostics;
using MarkPort.Core.Convert;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Settings;
using MarkPort.Core.Tests.Fixtures;
using MarkPort.Core.Vlm;
using Xunit.Abstractions;
using static MarkPort.Core.Tests.Convert.ConvertTestHelper;

namespace MarkPort.Core.Tests.Pipeline;

/// <summary>SC-004: running the steps as a pipeline is faster than running them one after another.</summary>
[Trait("Engine", "LibreOffice")]
public class ParallelismTests(ITestOutputHelper output)
{
    private const int Documents = 8;
    private const int PagesPerDocument = 3;

    private static AppSettings Settings(FakeOpenAiServer server)
    {
        var settings = AppSettings.CreateDefault();
        settings.Vlm.Connections.Add(new ConnectionProfile { Name = "fake", BaseUrl = server.BaseUrl, Model = "vision-model", TimeoutSeconds = 30 });
        settings.Vlm.Selected = "fake";
        return settings;
    }

    [LibreOfficeFact]
    public async Task ThePipeline_FinishesInUnder80PercentOfTheSequentialTime_AndStepsOverlap()
    {
        using var dir = new TempDir();
        var files = new List<string>();
        for (var i = 1; i <= Documents; i++)
        {
            var path = dir.Combine("in", $"d{i}.docx");
            DocxFactory.CreatePaged(path, [.. Enumerable.Range(1, PagesPerDocument).Select(p => new[] { $"doc {i} page {p}" })]);
            files.Add(path);
        }

        // The VLM takes a while per page, as a local model does.
        using var server = new FakeOpenAiServer { ChatDelay = TimeSpan.FromMilliseconds(250) };
        var (setup, error) = VlmSetupFactory.Create(Settings(server));
        Assert.True(setup is not null, error);
        using var engine = Engine(dir);

        // (a) One file after the other, each with all its steps: no step of one file overlaps another file.
        var sequentialWatch = Stopwatch.StartNew();
        foreach (var file in files)
        {
            var one = await new ConvertService(engine, vlm: setup).RunAsync(Request(dir.Combine("seq-out"), Options(step3: true, connection: "fake"), file));
            Assert.True(one.Documents[0].IsDone());
        }

        sequentialWatch.Stop();

        // (b) All files in one run: while one file is in step 3, the next ones are already in step 1.
        var overlap = false;
        var service = new ConvertService(engine, vlm: setup);
        var states = new Dictionary<string, (StepStatus One, StepStatus Three)>();
        service.DocumentChanged += d =>
        {
            lock (states)
            {
                states[d.DocId] = (d.Step1, d.Step3);
                if (states.Values.Any(s => s.Three == StepStatus.Running) && states.Values.Any(s => s.One == StepStatus.Running))
                {
                    overlap = true;
                }
            }
        };
        var pipelineWatch = Stopwatch.StartNew();
        var result = await service.RunAsync(Request(dir.Combine("pipe-out"), Options(step3: true, connection: "fake"), [.. files]));
        pipelineWatch.Stop();

        output.WriteLine($"sequential {sequentialWatch.Elapsed.TotalSeconds:0.0}s, pipeline {pipelineWatch.Elapsed.TotalSeconds:0.0}s ({100.0 * pipelineWatch.Elapsed / sequentialWatch.Elapsed:0}%)");
        Assert.All(result.Documents, d => Assert.True(d.IsDone()));
        Assert.True(overlap, "no file was in step 3 while another was in step 1");
        Assert.True(
            pipelineWatch.Elapsed < sequentialWatch.Elapsed * 0.8,
            $"the pipeline took {pipelineWatch.Elapsed.TotalSeconds:0.0}s; the sequential run {sequentialWatch.Elapsed.TotalSeconds:0.0}s (limit 80%)");
        setup!.Dispose();
    }
}
