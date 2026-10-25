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
using MarkPort.Core.Engines;
using MarkPort.Core.Jobs.Models;
using MarkPort.Core.Ooxml;
using MarkPort.Core.Pdf;
using MarkPort.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace MarkPort.Core.Tests.Engines;

/// <summary>
/// Microsoft Office through COM. These tests start Word, Excel and PowerPoint (without windows), so they run only on a
/// computer that has Office. They also settle spikes S1 (Word's own page numbers), S2 (Excel) and S4 (a user's Word stays open).
/// </summary>
[Trait("Engine", "Office")]
public class OfficeEngineTests(ITestOutputHelper output)
{
    private static PdfExportRequest Request(string source, DocumentKind kind, string pdf) =>
        new(source, kind, pdf, TimeSpan.FromMinutes(2));

    /// <summary>Runs COM code of the test itself on a thread in a single-threaded apartment.</summary>
    private static Task<T> Sta<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static int Count(string processName) => Process.GetProcessesByName(processName).Length;

    /// <summary>One long-lived STA thread: a COM object must be used on the thread that created it.</summary>
    private sealed class StaSession : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = [];
        private readonly Thread _thread;

        public StaSession()
        {
            _thread = new Thread(() =>
            {
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    work();
                }
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public Task<T> Run<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try
                {
                    completion.SetResult(work());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            return completion.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [OfficeFact]
    public void CheckAvailability_IsAvailable_WhenOfficeIsInstalled()
    {
        using var engine = new OfficeEngine();

        Assert.True(engine.CheckAvailability().IsAvailable);
    }

    [OfficeFact]
    public async Task Docx_BecomesAPdfWithOnePagePerPageBreak()
    {
        using var dir = new TempDir();
        var source = dir.Combine("in 文書.docx");
        DocxFactory.CreatePaged(source, ["P1-a", "P1-b"], ["P2-a"], ["P3-a"]);
        using var engine = new OfficeEngine();
        var pdf = dir.Combine("out", "a.pdf");

        await engine.ExportPdfAsync(Request(source, DocumentKind.Docx, pdf), CancellationToken.None);

        var pages = PdfTextExtractor.GetPageTexts(pdf);
        Assert.Equal(3, pages.Count);
        Assert.Contains("P1-a", pages[0]);
        Assert.Contains("P2-a", pages[1]);
        Assert.Contains("P3-a", pages[2]);
    }

    [OfficeFact]
    public async Task Pptx_HiddenSlidesAreNotExported_ByDefault()
    {
        using var dir = new TempDir();
        var source = dir.Combine("deck.pptx");
        PptxFactory.Create(source, new SlideSpec("First", ["one"]), new SlideSpec("Hidden", ["secret"], Hidden: true), new SlideSpec("Third", ["three"]));
        using var engine = new OfficeEngine();
        var pdf = dir.Combine("deck.pdf");

        await engine.ExportPdfAsync(Request(source, DocumentKind.Pptx, pdf), CancellationToken.None);

        var pages = PdfTextExtractor.GetPageTexts(pdf);
        Assert.Equal(2, pages.Count);
        Assert.DoesNotContain("secret", string.Concat(pages));
    }

    [OfficeFact]
    public async Task Pptx_ACopyWithTheHiddenSlidesShown_ExportsAllSlides()
    {
        using var dir = new TempDir();
        var source = dir.Combine("deck.pptx");
        PptxFactory.Create(source, new SlideSpec("First", ["one"]), new SlideSpec("Hidden", ["secret"], Hidden: true));
        var copy = dir.Combine("work", "shown.pptx");
        PptxTempCopy.CreateWithHiddenSlidesShown(source, copy);
        using var engine = new OfficeEngine();
        var pdf = dir.Combine("deck.pdf");

        await engine.ExportPdfAsync(Request(copy, DocumentKind.Pptx, pdf), CancellationToken.None);

        var pages = PdfTextExtractor.GetPageTexts(pdf);
        Assert.Equal(2, pages.Count);
        Assert.Contains("secret", string.Concat(pages));
    }

    [OfficeFact]
    public async Task Xlsx_BecomesAPdf()
    {
        using var dir = new TempDir();
        var source = dir.Combine("book.xlsx");
        XlsxFactory.Create(source, new SheetSpec("Data", [["Name", "Qty"], ["Apple", "3"]]));
        using var engine = new OfficeEngine();
        var pdf = dir.Combine("book.pdf");

        await engine.ExportPdfAsync(Request(source, DocumentKind.Xlsx, pdf), CancellationToken.None);

        Assert.Contains("Apple", string.Concat(PdfTextExtractor.GetPageTexts(pdf)));
    }

    // ---- Spike S2: the copy of the workbook gives the same ranges and parts as with LibreOffice ----

    [OfficeFact]
    public async Task S2_TheTargetSheetAloneIsPrinted_EvenWhenAnotherSheetComesFirstAndHasAPrintArea()
    {
        using var dir = new TempDir();
        var source = dir.Combine("book.xlsx");
        XlsxFactory.Create(
            source,
            new SheetSpec("Other", [["other-sheet-text"]], PrintArea: "A1:A1"),
            XlsxFactory.WideSheet("Wide", columns: 4, rows: 2));
        var sheet = XlsxReader.Read(source, false).Single(s => s.Name == "Wide");
        var unit = ExcelUnitPlanner.Plan(sheet).Single();
        var copy = dir.Combine("work", "copy.xlsx");
        ExcelTempCopy.Create(source, copy, sheet, unit);
        using var engine = new OfficeEngine();
        var pdf = dir.Combine("wide.pdf");

        await engine.ExportPdfAsync(Request(copy, DocumentKind.Xlsx, pdf), CancellationToken.None);

        var page = Assert.Single(PdfTextExtractor.GetPageTexts(pdf));
        Assert.Contains("H1", page);
        Assert.DoesNotContain("other-sheet-text", page);
    }

    [OfficeFact]
    public async Task S2_AnExtremelyWideSheet_GivesTheSamePartsAndColumnsAsLibreOffice()
    {
        using var dir = new TempDir();
        var source = dir.Combine("book.xlsx");
        XlsxFactory.Create(source, XlsxFactory.WideSheet("Wide", columns: 60, rows: 3, columnWidthChars: 14));
        var sheet = XlsxReader.Read(source, false).Single();
        var units = ExcelUnitPlanner.Plan(sheet);
        Assert.Equal(3, units.Count);
        using var office = new OfficeEngine();

        var officeColumns = new List<int[]>();
        foreach (var unit in units)
        {
            var copy = dir.Combine("work", $"office{unit.Part}.xlsx");
            ExcelTempCopy.Create(source, copy, sheet, unit);
            var pdf = Path.ChangeExtension(copy, ".pdf");
            await office.ExportPdfAsync(Request(copy, DocumentKind.Xlsx, pdf), CancellationToken.None);
            var pages = PdfTextExtractor.GetPageTexts(pdf);
            Assert.Single(pages); // every part is one page, as with LibreOffice
            officeColumns.Add([.. Enumerable.Range(1, 60).Where(c => ContainsToken(pages[0], $"r3c{c}"))]);
        }

        output.WriteLine("Office columns per part: " + string.Join(" | ", officeColumns.Select(c => $"{c.Min()}-{c.Max()} ({c.Length})")));
        Assert.Equal(
            units.Select(u => Enumerable.Range(u.FromColumnIndex + 1, u.ToColumnIndex - u.FromColumnIndex + 1).ToArray()),
            officeColumns);
    }

    private static bool ContainsToken(string text, string token)
    {
        for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + 1, StringComparison.Ordinal))
        {
            var next = i + token.Length;
            if (next >= text.Length || !char.IsDigit(text[next]))
            {
                return true;
            }
        }

        return false;
    }

    // ---- no dialogs ----

    [OfficeFact]
    public async Task APasswordProtectedDocx_FailsWithoutADialog_AndTheNextFileStillWorks()
    {
        using var dir = new TempDir();
        var plain = dir.Combine("plain.docx");
        DocxFactory.CreatePaged(plain, ["plain text"]);
        var locked = dir.Combine("locked.docx");
        await Sta(() =>
        {
            dynamic word = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
            try
            {
                word.Visible = false;
                word.DisplayAlerts = 0;
                dynamic document = word.Documents.Open(plain);
                document.SaveAs2(FileName: locked, Password: "secret-pw-123");
                document.Close(SaveChanges: 0);
            }
            finally
            {
                word.Quit(SaveChanges: 0);
            }

            return true;
        });
        using var engine = new OfficeEngine();
        var watch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExportPdfAsync(Request(locked, DocumentKind.Docx, dir.Combine("locked.pdf")), CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), "it must fail at once, not wait on a dialog");
        Assert.True(error.IsPasswordProtected, error.Message);
        Assert.False(File.Exists(dir.Combine("locked.pdf")));

        // The engine is still usable.
        await engine.ExportPdfAsync(Request(plain, DocumentKind.Docx, dir.Combine("plain.pdf")), CancellationToken.None);
        Assert.Contains("plain text", string.Concat(PdfTextExtractor.GetPageTexts(dir.Combine("plain.pdf"))));
    }

    // ---- Spike S4: the user's own Word is left alone ----

    [OfficeFact]
    public async Task S4_AWordThatTheUserHasOpen_StaysOpen_WithItsDocument()
    {
        using var dir = new TempDir();
        var usersDocument = dir.Combine("users.docx");
        DocxFactory.CreatePaged(usersDocument, ["the user is working on this"]);
        var source = dir.Combine("to-convert.docx");
        DocxFactory.CreatePaged(source, ["convert me"]);

        // "The user's Word": an instance that already exists, with a document open. It lives on its own STA thread.
        using var users = new StaSession();
        var state = await users.Run(() =>
        {
            dynamic usersWord = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
            usersWord.Visible = false;
            usersWord.Documents.Open(usersDocument, ReadOnly: true);
            return usersWord;
        });
        try
        {
            using (var engine = new OfficeEngine())
            {
                await engine.ExportPdfAsync(Request(source, DocumentKind.Docx, dir.Combine("out.pdf")), CancellationToken.None);
            } // disposing the engine quits only what it started

            var (documents, name) = await users.Run(() => ((int)state.Documents.Count, (string)state.Documents[1].Name));
            Assert.Equal(1, documents);
            Assert.Equal("users.docx", name);
            Assert.True(Count("WINWORD") >= 1);
        }
        finally
        {
            await users.Run(() =>
            {
                try
                {
                    state.Quit(SaveChanges: 0);
                }
                catch (Exception)
                {
                    // already closed
                }

                return true;
            });
        }
    }

    [OfficeFact]
    public async Task TheOfficeProcessesThatTheEngineStarted_AreGoneAfterItIsDisposed()
    {
        using var dir = new TempDir();
        var docx = dir.Combine("a.docx");
        DocxFactory.CreatePaged(docx, ["x"]);
        var xlsx = dir.Combine("a.xlsx");
        XlsxFactory.Create(xlsx, new SheetSpec("S", [["v"]]));
        var before = (Count("WINWORD"), Count("EXCEL"));

        using (var engine = new OfficeEngine())
        {
            await engine.ExportPdfAsync(Request(docx, DocumentKind.Docx, dir.Combine("a1.pdf")), CancellationToken.None);
            await engine.ExportPdfAsync(Request(xlsx, DocumentKind.Xlsx, dir.Combine("a2.pdf")), CancellationToken.None);
        }

        // The processes need a moment to end.
        var after = (Count("WINWORD"), Count("EXCEL"));
        for (var i = 0; i < 20 && after != before; i++)
        {
            await Task.Delay(500);
            after = (Count("WINWORD"), Count("EXCEL"));
        }

        output.WriteLine($"Word/Excel processes: before {before}, after {after}");
        Assert.Equal(before, after);
    }

    [OfficeFact]
    public async Task AMissingFile_FailsClearly()
    {
        using var dir = new TempDir();
        using var engine = new OfficeEngine();

        var error = await Assert.ThrowsAsync<EngineException>(() =>
            engine.ExportPdfAsync(Request(dir.Combine("none.docx"), DocumentKind.Docx, dir.Combine("o.pdf")), CancellationToken.None));

        Assert.Contains("not found", error.Message);
    }

    [OfficeFact]
    public async Task TheSourceFile_IsNeverChanged()
    {
        using var dir = new TempDir();
        var source = dir.Combine("a.docx");
        DocxFactory.CreatePaged(source, ["x"]);
        var bytes = File.ReadAllBytes(source);
        var time = File.GetLastWriteTimeUtc(source);
        using var engine = new OfficeEngine();

        await engine.ExportPdfAsync(Request(source, DocumentKind.Docx, dir.Combine("a.pdf")), CancellationToken.None);

        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Equal(time, File.GetLastWriteTimeUtc(source));
        Assert.Equal([source], Directory.GetFiles(dir.Path, "*.docx"));
    }

    // ---- Spike S1, the Word side: Word's own page numbers are the ground truth ----

    [OfficeFact]
    public async Task S1_WordsOwnPageNumbers_AreMatchedByThePageAssignment()
    {
        using var dir = new TempDir();
        var source = dir.Combine("flow.docx");
        DocxFactory.CreateFlow(source, paragraphs: 120, wordsPerParagraph: 60);

        // Word says on which page each paragraph starts (Range.Information(wdActiveEndPageNumber), start of the paragraph).
        var truth = await Sta(() =>
        {
            dynamic word = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
            try
            {
                word.Visible = false;
                word.DisplayAlerts = 0;
                dynamic document = word.Documents.Open(source, ReadOnly: true);
                var pages = new List<int>();
                int count = document.Paragraphs.Count;
                for (var i = 1; i <= count; i++)
                {
                    dynamic range = document.Paragraphs[i].Range;
                    range.Collapse(1); // wdCollapseStart
                    pages.Add((int)range.Information(3)); // wdActiveEndPageNumber
                }

                document.Close(SaveChanges: 0);
                return pages;
            }
            finally
            {
                word.Quit(SaveChanges: 0);
            }
        });

        using var engine = new OfficeEngine();
        var pdf = dir.Combine("flow.pdf");
        await engine.ExportPdfAsync(Request(source, DocumentKind.Docx, pdf), CancellationToken.None);
        var blocks = DocxReader.Read(source);
        var warnings = DocxPageAssigner.Assign(blocks, PdfTextExtractor.GetPageTexts(pdf));

        var correct = blocks.Select((b, i) => b.Block.PageIndex == truth[i]).Count(x => x);
        var rate = 100.0 * correct / blocks.Count;
        output.WriteLine($"S1 (Word): {blocks.Count} paragraphs, {truth.Max()} pages by Word: {correct} correct ({rate:0.0}%), {warnings.Count} warnings");
        Assert.Equal(blocks.Count, truth.Count);
        Assert.True(rate >= 99.0, $"page assignment accuracy against Word's own page numbers is {rate:0.0}%");
    }
}
