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

using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Cli.Tests;

public class ConsoleProgressTests
{
    private static DocumentRecord Doc(params PageRecord[] pages) => new()
    {
        DocId = "d0001",
        SourcePath = @"D:\in.docx",
        Kind = DocumentKind.Docx,
        Step1 = StepStatus.Done,
        Step2 = StepStatus.Done,
        Step3 = StepStatus.Running,
        Pages = [.. pages],
    };

    private static PageRecord Page(int index, PageStep3Status status, UsageInfo? usage = null, bool deleted = false) =>
        new() { Index = index, Step3 = status, Usage = usage, IsDeleted = deleted };

    [Fact]
    public void WhileStep3Runs_EachFinishedPageGetsALine_WithItsTokens()
    {
        var writer = new StringWriter();
        var progress = new ConsoleProgress(writer, quiet: false);

        progress.Report(Doc(Page(1, PageStep3Status.Pending), Page(2, PageStep3Status.Pending, deleted: true), Page(3, PageStep3Status.Pending)));
        progress.Report(Doc(Page(1, PageStep3Status.Done, new UsageInfo(820, 240, 1060)), Page(2, PageStep3Status.Skipped, deleted: true), Page(3, PageStep3Status.Pending)));
        progress.Report(Doc(Page(1, PageStep3Status.Done, new UsageInfo(820, 240, 1060)), Page(2, PageStep3Status.Skipped, deleted: true), Page(3, PageStep3Status.Failed)));

        var text = writer.ToString();
        Assert.Contains("step3  page=1/2  tokens=prompt:820 completion:240 total:1060", text, StringComparison.Ordinal);
        Assert.Contains("step3  page=2/2  failed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AReportThatChangesNothing_PrintsNoNewLine()
    {
        var writer = new StringWriter();
        var progress = new ConsoleProgress(writer, quiet: false);
        var document = Doc(Page(1, PageStep3Status.Done));

        progress.Report(document);
        progress.Report(document);

        Assert.Equal(1, writer.ToString().Split("page=1/1").Length - 1);
    }

    [Fact]
    public void WithQuiet_NoPageLinesArePrinted()
    {
        var writer = new StringWriter();
        var progress = new ConsoleProgress(writer, quiet: true);

        progress.Report(Doc(Page(1, PageStep3Status.Done)));

        Assert.DoesNotContain("page=", writer.ToString(), StringComparison.Ordinal);
    }
}
