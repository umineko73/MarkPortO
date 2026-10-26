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

using MarkPortO.Core.Localization;
using System.Globalization;
using MarkPortO.Core.Convert;
using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Pipeline;

/// <summary>The state of a file as the file list shows it (FR-013).</summary>
public enum FileDisplayState
{
    /// <summary>待機中: nothing has started.</summary>
    Waiting,

    /// <summary>Step1の実行中.</summary>
    Step1Running,

    /// <summary>Step2の実行中.</summary>
    Step2Running,

    /// <summary>Step3の実行中.</summary>
    Step3Running,

    /// <summary>完了.</summary>
    Done,

    /// <summary>完了（一部失敗）: the file is finished but some pages failed (FR-013b); see <see cref="FileRow.Message"/>.</summary>
    DoneWithPageFailures,

    /// <summary>失敗: see <see cref="FileRow.Message"/>.</summary>
    Failed,

    /// <summary>中断.</summary>
    Interrupted,
}

/// <summary>One page of a file in the detail view.</summary>
/// <param name="Index">Page number in the output.</param>
/// <param name="Label">Where the page is in the source.</param>
/// <param name="IsDeleted">A deleted page (a "×" over it).</param>
/// <param name="VlmState">The VLM reading of the page: done, failed, skipped, or waiting.</param>
/// <param name="UsageText">The tokens used, or 取得不可; the cause when the page failed; empty when the VLM is not used.</param>
public sealed record PageRow(int Index, string Label, bool IsDeleted, string VlmState, string UsageText);

/// <summary>One row of the file list.</summary>
/// <param name="DocId">Identifier of the file within the job.</param>
/// <param name="FileName">What the list shows: the path from the given folder, or the file name (FR-013a).</param>
/// <param name="Kind">docx, xlsx or pptx.</param>
/// <param name="State">The state to show.</param>
/// <param name="StepStates">The state of steps 1 to 3 (対象外 is <see cref="StepStatus.NotApplicable"/>).</param>
/// <param name="PageCount">Number of pages (images) so far.</param>
/// <param name="DeletedPageCount">How many of them are deleted pages.</param>
/// <param name="Message">The reason of a failure, or the note about a restart; empty otherwise.</param>
/// <param name="UsageText">Total VLM usage of the file; <c>取得不可</c> when step 3 finished or failed without it; empty before that and when step 3 is not used.</param>
/// <param name="Pages">The pages, for the detail view.</param>
/// <param name="FullPath">The full path of the source, for the tooltip.</param>
/// <param name="Source">The document the row was made from, to make the texts again when the language changes.</param>
/// <param name="Step3Read">How many pages step 3 has finished (read or failed); the deleted pages are not counted.</param>
/// <param name="Step3Total">How many pages step 3 reads; the deleted pages are not counted.</param>
public sealed record FileRow(
    string DocId,
    string FileName,
    DocumentKind Kind,
    FileDisplayState State,
    IReadOnlyList<StepStatus> StepStates,
    int PageCount,
    int DeletedPageCount,
    string Message,
    string UsageText,
    IReadOnlyList<PageRow> Pages,
    string FullPath = "",
    DocumentRecord? Source = null,
    int Step3Read = 0,
    int Step3Total = 0);

/// <summary>Turns the saved state of a document into the row the file list shows (FR-013, FR-022).</summary>
public static class FileStatusProjector
{
    public static string UnavailableText => Loc.T("unavailable");

    public static FileRow Project(DocumentRecord document)
    {
        var steps = new[] { document.Step1, document.Step2, document.Step3 };
        var usesVlm = document.Step3 != StepStatus.NotApplicable;

        return new FileRow(
            document.DocId,
            document.Display(),
            document.Kind,
            StateOf(document),
            steps,
            document.Pages.Count,
            document.Pages.Count(p => p.IsDeleted),
            MessageOf(document),
            usesVlm ? FileUsageText(document) : string.Empty,
            [.. document.Pages.Select(p => ToRow(p, usesVlm))],
            document.SourcePath,
            document,
            Step3Progress(document).Read,
            Step3Progress(document).Total);
    }

    public static FileDisplayState StateOf(DocumentRecord document)
    {
        if (document.IsFailed())
        {
            return FileDisplayState.Failed;
        }

        if (document.Step1 == StepStatus.Running)
        {
            return FileDisplayState.Step1Running;
        }

        if (document.Step2 == StepStatus.Running)
        {
            return FileDisplayState.Step2Running;
        }

        if (document.Step3 == StepStatus.Running)
        {
            return FileDisplayState.Step3Running;
        }

        if (document.IsDone())
        {
            return document.Pages.Any(p => p.Step3 == PageStep3Status.Failed) ? FileDisplayState.DoneWithPageFailures : FileDisplayState.Done;
        }

        var interrupted = document.Step1 == StepStatus.Interrupted
            || document.Step2 == StepStatus.Interrupted
            || document.Step3 == StepStatus.Interrupted;
        return interrupted ? FileDisplayState.Interrupted : FileDisplayState.Waiting;
    }

    /// <summary>The Japanese text for a state, as shown on the screen.</summary>
    public static string Text(FileDisplayState state) => state switch
    {
        FileDisplayState.Waiting => Loc.T("Waiting"),
        FileDisplayState.Step1Running => Loc.T("Step1 running"),
        FileDisplayState.Step2Running => Loc.T("Step2 running"),
        FileDisplayState.Step3Running => Loc.T("Step3 running"),
        FileDisplayState.Done => Loc.T("Done"),
        FileDisplayState.DoneWithPageFailures => Loc.T("Done (some pages failed)"),
        FileDisplayState.Failed => Loc.T("Failed"),
        FileDisplayState.Interrupted => Loc.T("Interrupted"),
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>The text for one step: 対象外 when it is not selected.</summary>
    public static string StepText(StepStatus status) => status switch
    {
        StepStatus.NotApplicable => Loc.T("Not applicable"),
        StepStatus.Pending => Loc.T("Waiting"),
        StepStatus.Running => Loc.T("Running"),
        StepStatus.Done => Loc.T("Done"),
        StepStatus.Failed => Loc.T("Failed"),
        StepStatus.Interrupted => Loc.T("Interrupted"),
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>How many of the pages step 3 reads are finished (read or failed); the deleted pages are not counted.</summary>
    public static (int Read, int Total) Step3Progress(DocumentRecord document) =>
        (document.Pages.Count(p => !p.IsDeleted && p.Step3 is PageStep3Status.Done or PageStep3Status.Failed),
         document.Pages.Count(p => !p.IsDeleted));

    /// <summary>The text of step 3 in the list: while it runs, how many of the pages are finished (<c>実行中 3/12</c>).</summary>
    public static string Step3Text(FileRow row) =>
        row.StepStates[2] == StepStatus.Running && row.Step3Total > 0
            ? Loc.T("Running {0}/{1}", row.Step3Read, row.Step3Total)
            : StepText(row.StepStates[2]);

    private static string MessageOf(DocumentRecord document)
    {
        if (document.Error is { } error)
        {
            return error.Message;
        }

        var failedPages = document.Pages.Where(p => p.Step3 == PageStep3Status.Failed).ToList();
        if (failedPages.Count > 0)
        {
            return Loc.T("{0} page(s) could not be read ({1}: {2}). Use \"Retry failed only\" to read only the failed pages.", failedPages.Count, PageLabel.Text(failedPages[0].SourceLabel, CultureInfo.CurrentUICulture), failedPages[0].Error);
        }

        var restarted = document.Warnings.FirstOrDefault(w => w.Code == Jobs.ResumePlanner.RestartedCode);
        return restarted is null ? string.Empty : Loc.T("The input file changed since the earlier run, so the earlier result was discarded and the file is converted again.");
    }

    /// <summary>The file total: the saved total, or the sum of the pages' usage when there is none yet.</summary>
    private static UsageInfo? TotalUsage(DocumentRecord document)
    {
        if (document.UsageTotal is { IsAvailable: true } saved)
        {
            return saved;
        }

        var known = document.Pages.Where(p => !p.IsDeleted && p.Usage is { IsAvailable: true }).Select(p => p.Usage!).ToList();
        if (known.Count == 0)
        {
            return null;
        }

        int? Sum(Func<UsageInfo, int?> pick) => known.Any(u => pick(u).HasValue) ? known.Sum(u => pick(u) ?? 0) : null;
        return new UsageInfo(Sum(u => u.PromptTokens), Sum(u => u.CompletionTokens), Sum(u => u.TotalTokens));
    }

    /// <summary>The usage of the file: the tokens when they are known; 取得不可 only once step 3 has finished or failed; empty before that.</summary>
    private static string FileUsageText(DocumentRecord document)
    {
        var total = TotalUsage(document);
        var cost = document.CostTotalUsd ?? SumCost(document);
        return total is null && cost is null && document.Step3 is not (StepStatus.Done or StepStatus.Failed) ? string.Empty : UsageText(total, cost);
    }

    /// <summary>The cost of the pages read so far, when the command reported it (FR-054); null when none did.</summary>
    private static double? SumCost(DocumentRecord document)
    {
        var known = document.Pages.Where(p => !p.IsDeleted && p.CostUsd.HasValue).Select(p => p.CostUsd!.Value).ToList();
        return known.Count == 0 ? null : known.Sum();
    }

    private static string UsageText(UsageInfo? usage, double? costUsd = null)
    {
        string Part(string name, int? value) => value.HasValue ? string.Create(CultureInfo.InvariantCulture, $"{name}={value.Value}") : string.Empty;
        var tokens = usage is { IsAvailable: true }
            ? string.Join(' ', new[] { Part("prompt", usage.PromptTokens), Part("completion", usage.CompletionTokens), Part("total", usage.TotalTokens) }.Where(t => t.Length > 0))
            : UnavailableText;
        return costUsd is { } cost ? string.Create(CultureInfo.InvariantCulture, $"{tokens} cost=${cost:0.####}") : tokens;
    }

    /// <summary>The usage of a page once it is read; for a page that failed, the cause (the column is shared); empty before that.</summary>
    private static string PageUsageText(PageRecord page) => page.Step3 switch
    {
        PageStep3Status.Done => UsageText(page.Usage, page.CostUsd),
        PageStep3Status.Failed => string.IsNullOrWhiteSpace(page.Error) ? UnavailableText : string.Join(' ', page.Error.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
        _ => string.Empty,
    };

    private static PageRow ToRow(PageRecord page, bool usesVlm)
    {
        var vlmState = page.IsDeleted
            ? Loc.T("Not applicable")
            : page.Step3 switch
            {
                PageStep3Status.Done => Loc.T("Done"),
                PageStep3Status.Failed => Loc.T("Failed"),
                PageStep3Status.Skipped => Loc.T("Not applicable"),
                _ => Loc.T("Waiting"),
            };

        return new PageRow(
            page.Index,
            PageLabel.Text(page.SourceLabel, CultureInfo.CurrentUICulture),
            page.IsDeleted,
            usesVlm ? vlmState : string.Empty,
            usesVlm && !page.IsDeleted ? PageUsageText(page) : string.Empty);
    }
}

/// <summary>
/// Collects the changes of documents and hands them on in batches at a fixed interval, so a screen is not flooded:
/// the changes of many files within one interval are one update, and the latest state of each file wins (SC-011).
/// </summary>
public sealed class FileStatusFeed : IDisposable
{
    /// <summary>The screen is updated at least this often while something changes (well inside the 2 seconds of SC-011).</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly Dictionary<string, FileRow> _pending = [];
    private readonly Timer _timer;
    private bool _disposed;

    public FileStatusFeed(TimeSpan? interval = null)
    {
        var period = interval ?? DefaultInterval;
        _timer = new Timer(_ => Flush(), null, period, period);
    }

    /// <summary>Raised from a timer thread with the rows that changed since the last batch (latest state of each).</summary>
    public event Action<IReadOnlyList<FileRow>>? Updated;

    /// <summary>Call this when a document changes (see <see cref="ConvertService.DocumentChanged"/>). Cheap and thread-safe.</summary>
    public void Post(DocumentRecord document)
    {
        var row = FileStatusProjector.Project(document);
        lock (_gate)
        {
            if (!_disposed)
            {
                _pending[row.DocId] = row;
            }
        }
    }

    /// <summary>Hands on what has been collected now (also done at the end of a run, so the last state is shown at once).</summary>
    public void Flush()
    {
        FileRow[] batch;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = [.. _pending.Values];
            _pending.Clear();
        }

        try
        {
            Updated?.Invoke(batch);
        }
        catch (Exception)
        {
            // A broken observer must not stop the conversion.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _timer.Dispose();
        Flush();
    }
}
