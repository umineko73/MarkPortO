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
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Engines;

/// <summary>
/// Converts documents to PDF with Microsoft Office through COM (research R11). Word, Excel and PowerPoint are driven
/// without a window, without dialogs and without macros, from one dedicated STA thread, so only one document is
/// converted at a time. An Office application that the user already had open is never closed: only the document this
/// engine opened is closed, and only a process this engine started is ever stopped.
/// </summary>
public sealed class OfficeEngine : IDocumentEngine, IDisposable
{
    private readonly StaThread _thread = new();
    private readonly Dictionary<DocumentKind, OfficeApp> _apps = [];

    public EngineKind Kind => EngineKind.Office;

    public EngineAvailability CheckAvailability()
    {
        var missing = new List<string>();
        foreach (var (progId, name) in new[] { ("Word.Application", "Word"), ("Excel.Application", "Excel"), ("PowerPoint.Application", "PowerPoint") })
        {
            if (Type.GetTypeFromProgID(progId) is null)
            {
                missing.Add(name);
            }
        }

        return missing.Count == 0
            ? EngineAvailability.Available
            : EngineAvailability.Unavailable(Loc.T("Microsoft Office is not installed ({0} not found). Choose LibreOffice.", string.Join(", ", missing)));
    }

    public async Task ExportPdfAsync(PdfExportRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.SourcePath))
        {
            throw new EngineException(Loc.T("The file was not found: {0}", request.SourcePath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPdfPath))!);
        var source = Path.GetFullPath(request.SourcePath);
        var target = Path.GetFullPath(request.OutputPdfPath);
        if (File.Exists(target))
        {
            File.Delete(target);
        }

        var work = _thread.Run(() => Export(request.Kind, source, target));
        var finished = await Task.WhenAny(work, Task.Delay(request.Timeout, cancellationToken)).ConfigureAwait(false);
        if (finished == work)
        {
            await work.ConfigureAwait(false); // rethrows an EngineException
            return;
        }

        // Timed out or cancelled: stop the Office process we started, which ends the call that is stuck.
        StopOwnProcess(request.Kind);
        try
        {
            await work.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The stuck call ends with an error once its process is gone; that error is not the cause.
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new EngineException(Loc.T("Microsoft Office did not finish within {0:0} seconds and was stopped.", request.Timeout.TotalSeconds))
        {
            TimedOut = true,
        };
    }

    public void Dispose()
    {
        IReadOnlyList<(string Name, IReadOnlyList<int> Ids)> owned = [];
        try
        {
            owned = _thread.Run(CloseAll).Wait(TimeSpan.FromSeconds(30)) ? _lastOwned : [];
        }
        catch (Exception)
        {
            // Closing is best effort; what is still running is stopped below.
            owned = _lastOwned;
        }

        // Quit gives the application a moment to end. What this engine started and is still running after that is stopped.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && owned.Any(o => o.Ids.Any(IsRunning)))
        {
            Thread.Sleep(200);
        }

        foreach (var (name, ids) in owned)
        {
            foreach (var id in ids.Where(IsRunning))
            {
                Kill(id, name);
            }
        }

        _thread.Dispose();
    }

    private static bool IsRunning(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void Kill(int id, string expectedName)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            if (string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
            // already gone
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }

    /// <summary>The Office processes this engine started, remembered while the engine is closed down.</summary>
    private IReadOnlyList<(string Name, IReadOnlyList<int> Ids)> _lastOwned = [];

    // ---- everything below runs on the STA thread ----

    private bool Export(DocumentKind kind, string source, string target)
    {
        try
        {
            var app = GetApp(kind);

            // Excel shows "Publishing..." while it writes a PDF, however it is set up: hide it, in the Excel that this program started (FR-062).
            using var hider = kind == DocumentKind.Xlsx && app.StartedByUs ? new ProgressDialogHider(app.OwnedProcessIds) : null;
            switch (kind)
            {
                case DocumentKind.Docx:
                    ExportWord(app.Instance, source, target);
                    break;
                case DocumentKind.Xlsx:
                    ExportExcel(app.Instance, source, target);
                    break;
                case DocumentKind.Pptx:
                    ExportPowerPoint(app.Instance, source, target);
                    break;
            }

            if (!File.Exists(target))
            {
                throw new EngineException(Loc.T("Microsoft Office did not produce a PDF."));
            }

            return true;
        }
        catch (EngineException)
        {
            throw;
        }
        catch (COMException ex)
        {
            // A document that cannot be opened (a password, a damaged file) leaves the application healthy: it stays
            // in use, and is still closed at the end. Only an application that is gone is dropped, so the next call
            // starts it again.
            if (IsApplicationGone(ex))
            {
                StopOwnProcess(kind);
            }

            throw Describe(ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or MissingMemberException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            throw new EngineException(Loc.T("Microsoft Office could not convert the file: {0}", ex.Message), ex);
        }
    }

    /// <summary>The errors COM gives when the server process has ended or the connection to it is lost.</summary>
    private static bool IsApplicationGone(COMException ex) =>
        ex.HResult is unchecked((int)0x800706BA) // the RPC server is unavailable
            or unchecked((int)0x800706BE) // the remote procedure call failed
            or unchecked((int)0x80010108) // the object invoked has disconnected from its clients
            or unchecked((int)0x80010012); // the server died

    private static EngineException Describe(COMException ex)
    {
        // The error codes Office gives for a file that needs a password or cannot be opened.
        var password = ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("パスワード", StringComparison.Ordinal);
        return new EngineException(
            password
                ? Loc.T("The document is protected by a password. It is reported as failed and no dialog is shown.")
                : Loc.T("Microsoft Office could not open or convert the file: {0}", ex.Message.Trim()),
            ex)
        {
            IsPasswordProtected = password,
        };
    }

    private static void ExportWord(dynamic word, string source, string target)
    {
        dynamic? document = null;
        try
        {
            // A password that cannot be right is given, so a protected document fails instead of asking.
            document = word.Documents.Open(
                FileName: source,
                ConfirmConversions: false,
                ReadOnly: true,
                AddToRecentFiles: false,
                PasswordDocument: "markporto-no-password",
                NoEncodingDialog: true,
                Visible: false);
            // Item 0: the content of the document, without markup (tracked changes, comments).
            document.ExportAsFixedFormat(
                OutputFileName: target,
                ExportFormat: 17, // wdExportFormatPDF
                OpenAfterExport: false,
                OptimizeFor: 0,
                Range: 0,
                Item: 0,
                IncludeDocProps: false,
                KeepIRM: false,
                CreateBookmarks: 0,
                DocStructureTags: false,
                BitmapMissingFonts: true,
                UseISO19005_1: false);
        }
        finally
        {
            CloseQuietly(() => document?.Close(SaveChanges: 0), (object?)document);
        }
    }

    private static void ExportExcel(dynamic excel, string source, string target)
    {
        dynamic? workbook = null;
        try
        {
            workbook = excel.Workbooks.Open(
                Filename: source,
                UpdateLinks: 0,
                ReadOnly: true,
                Password: "markporto-no-password",
                IgnoreReadOnlyRecommended: true,
                AddToMru: false);
            workbook.ExportAsFixedFormat(
                Type: 0, // xlTypePDF
                Filename: target,
                Quality: 0,
                IncludeDocProperties: false,
                IgnorePrintAreas: false,
                OpenAfterPublish: false);
        }
        finally
        {
            CloseQuietly(() => workbook?.Close(SaveChanges: false), (object?)workbook);
        }
    }

    private static void ExportPowerPoint(dynamic powerPoint, string source, string target)
    {
        dynamic? presentation = null;
        try
        {
            presentation = powerPoint.Presentations.Open(
                FileName: source,
                ReadOnly: -1, // msoTrue
                Untitled: 0,
                WithWindow: 0); // msoFalse: no window
            // A copy is saved as PDF (format 32): the presentation itself is not changed. Hidden slides are left out by
            // PowerPoint; when they are wanted, the caller passes a copy in which they are shown.
            presentation.SaveCopyAs(target, 32);
        }
        finally
        {
            CloseQuietly(() => presentation?.Close(), (object?)presentation);
        }
    }

    private static void CloseQuietly(Action close, object? comObject)
    {
        try
        {
            close();
        }
        catch (Exception)
        {
            // The document may already be gone.
        }

        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    private OfficeApp GetApp(DocumentKind kind)
    {
        if (_apps.TryGetValue(kind, out var existing))
        {
            return existing;
        }

        var (progId, processName) = kind switch
        {
            DocumentKind.Docx => ("Word.Application", "WINWORD"),
            DocumentKind.Xlsx => ("Excel.Application", "EXCEL"),
            DocumentKind.Pptx => ("PowerPoint.Application", "POWERPNT"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var type = Type.GetTypeFromProgID(progId)
            ?? throw new EngineException(Loc.T("{0} is not installed.", progId));

        // The processes of this kind that exist now belong to the user: they are never stopped or quit.
        var before = Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet();
        dynamic instance = Activator.CreateInstance(type)!;
        var owned = Process.GetProcessesByName(processName).Select(p => p.Id).Where(id => !before.Contains(id)).ToList();
        var startedByUs = owned.Count > 0;

        Configure(kind, instance);
        var app = new OfficeApp(instance, processName, startedByUs, owned);
        _apps[kind] = app;
        return app;
    }

    private static void Configure(DocumentKind kind, dynamic app)
    {
        switch (kind)
        {
            case DocumentKind.Docx:
                app.Visible = false;
                app.DisplayAlerts = 0; // wdAlertsNone
                app.AutomationSecurity = 3; // msoAutomationSecurityForceDisable: no macros
                break;
            case DocumentKind.Xlsx:
                app.Visible = false;
                app.DisplayAlerts = false;
                app.AskToUpdateLinks = false;
                app.EnableEvents = false;
                app.AutomationSecurity = 3;
                break;
            case DocumentKind.Pptx:
                app.DisplayAlerts = 1; // ppAlertsNone
                app.AutomationSecurity = 3;
                break;
        }
    }

    private bool CloseAll()
    {
        _lastOwned = [.. _apps.Values.Where(a => a.StartedByUs).Select(a => (a.ProcessName, a.OwnedProcessIds))];
        foreach (var (kind, app) in _apps.ToList())
        {
            try
            {
                // Quit only an application this engine started. The user's own is left as it is.
                if (app.StartedByUs)
                {
                    switch (kind)
                    {
                        case DocumentKind.Docx:
                            app.Instance.Quit(SaveChanges: 0);
                            break;
                        default:
                            app.Instance.Quit();
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // best effort
            }

            if (Marshal.IsComObject(app.Instance))
            {
                Marshal.FinalReleaseComObject(app.Instance);
            }
        }

        _apps.Clear();
        return true;
    }

    /// <summary>Stops the processes this engine started for a kind of document (never the user's own).</summary>
    private void StopOwnProcess(DocumentKind kind)
    {
        OfficeApp? app;
        lock (_apps)
        {
            _apps.TryGetValue(kind, out app);
        }

        if (app is not { StartedByUs: true })
        {
            return;
        }

        foreach (var id in app.OwnedProcessIds)
        {
            Kill(id, app.ProcessName);
        }

        _apps.Remove(kind);
    }

    private sealed record OfficeApp(dynamic Instance, string ProcessName, bool StartedByUs, IReadOnlyList<int> OwnedProcessIds);

    /// <summary>One thread in a single-threaded apartment: COM objects of Office must be used from the thread that made them.</summary>
    private sealed class StaThread : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = [];
        private readonly Thread _thread;

        public StaThread()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "MarkPortO Office (STA)" };
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

        private void Loop()
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                work();
            }
        }
    }
}
