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
using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Settings;
using MarkPortO.Core.Tests.Fixtures;
using MarkPortO.Core.Vlm;

namespace MarkPortO.Core.Tests.Convert;

public class ReviewResumeTests
{
    private sealed class Engine : IDocumentEngine
    {
        public EngineKind Kind => EngineKind.LibreOffice;
        public Action? Checking { get; init; }
        public EngineAvailability CheckAvailability() { Checking?.Invoke(); return EngineAvailability.Available; }
        public Task ExportPdfAsync(PdfExportRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static (StateStore Store, JobState Job) Saved(TempDir dir, DocumentKind kind)
    {
        var source = dir.Combine("source." + kind.ToString().ToLowerInvariant());
        File.WriteAllText(source, "unchanged");
        var store = new StateStore(dir.Combine("out"));
        var document = new DocumentRecord { DocId = "d1", SourcePath = source, Kind = kind,
            OutputFolder = dir.Combine("out", "doc"), Fingerprint = StateStore.ComputeFingerprint(source),
            Step1 = StepStatus.Done, Step2 = StepStatus.NotApplicable, Step3 = StepStatus.NotApplicable };
        var job = new JobState { JobId = Guid.NewGuid(), Status = JobStatus.Interrupted, Documents = ["d1"],
            Options = new OptionsSnapshot { Step2 = false, Step3 = false } };
        store.SaveJob(job);
        store.SaveDocument(job.JobId, document);
        return (store, job);
    }

    [Fact]
    public async Task PdfResume_DoesNotRequireAnOfficeEngine()
    {
        using var dir = new TempDir();
        var (store, job) = Saved(dir, DocumentKind.Pdf);
        var service = new ConvertService(new UnavailableEngine(EngineKind.Office, "not installed"));
        var result = await service.ResumeAsync(new ResumeRequest { OutputFolder = store.OutRoot, JobId = job.JobId });
        Assert.Equal(JobStatus.Completed, result.Job.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_RejectsMissingOrRevokedExtractor(bool removed)
    {
        using var dir = new TempDir();
        var (store, job) = Saved(dir, DocumentKind.Docx);
        job.Options.Extractors.Docx = "external";
        store.SaveJob(job);
        var profile = new ExtractorProfile { Name = "external", Command = Environment.ProcessPath!,
            Args = ["{input}"], Output = ExtractorOutput.Stdout, SendsExternally = true };
        ExternalConsent.Grant(profile, DateTimeOffset.UtcNow);
        ExternalConsent.Revoke(profile);
        var service = new ConvertService(new Engine(), extractors: removed ? [] : [profile]);
        await Assert.ThrowsAsync<ConvertStartException>(() => service.ResumeAsync(new ResumeRequest { OutputFolder = store.OutRoot, JobId = job.JobId }));
        Assert.Equal(JobStatus.Interrupted, store.LoadJob(job.JobId)!.Status);
    }

    [Fact]
    public async Task Resume_HoldsLockBeforeCheckingEngineOrChangingDocuments()
    {
        using var dir = new TempDir();
        var (store, job) = Saved(dir, DocumentKind.Docx);
        var checkedLock = false;
        var engine = new Engine { Checking = () => { checkedLock = true; Assert.True(store.IsLocked(job.JobId)); } };
        await new ConvertService(engine).ResumeAsync(new ResumeRequest { OutputFolder = store.OutRoot, JobId = job.JobId });
        Assert.True(checkedLock);
        Assert.False(store.IsLocked(job.JobId));
    }
}
