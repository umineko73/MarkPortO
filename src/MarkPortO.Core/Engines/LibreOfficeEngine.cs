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
using System.Diagnostics;
using System.Text;
using MarkPortO.Core.Jobs.Models;

namespace MarkPortO.Core.Engines;

/// <summary>
/// Converts documents to PDF with LibreOffice running without a window (research R12).
/// Several conversions can run at once; each one gets its own user profile folder, because
/// two LibreOffice processes cannot share one.
/// </summary>
public sealed class LibreOfficeEngine : IDocumentEngine, IDisposable
{
    public const int DefaultMaxParallel = 2;

    private readonly string? _configuredPath;
    private readonly string _profileRoot;
    private readonly SemaphoreSlim _slots;

    public LibreOfficeEngine(string? configuredPath = null, int maxParallel = DefaultMaxParallel, string? profileRoot = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallel, 1);
        _configuredPath = configuredPath;
        MaxParallel = maxParallel;
        _profileRoot = profileRoot
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkPortO", "libreoffice-profiles");
        _slots = new SemaphoreSlim(maxParallel, maxParallel);
    }

    public EngineKind Kind => EngineKind.LibreOffice;

    /// <summary>How many conversions may run at the same time.</summary>
    public int MaxParallel { get; }

    public EngineAvailability CheckAvailability() =>
        LibreOfficeLocator.Find(_configuredPath) is null
            ? EngineAvailability.Unavailable(
                string.IsNullOrWhiteSpace(_configuredPath)
                    ? Loc.T("LibreOffice (soffice.exe) was not found. Install it or set engine.libreOfficePath.")
                    : Loc.T("LibreOffice was not found at '{0}'.", _configuredPath))
            : EngineAvailability.Available;

    public async Task ExportPdfAsync(PdfExportRequest request, CancellationToken cancellationToken)
    {
        var soffice = LibreOfficeLocator.Find(_configuredPath)
            ?? throw new EngineException(CheckAvailability().Reason ?? Loc.T("LibreOffice was not found."));
        if (!File.Exists(request.SourcePath))
        {
            throw new EngineException(Loc.T("The file was not found: {0}", request.SourcePath));
        }

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var outputDirectory = Path.Combine(Path.GetTempPath(), "markporto-lo-out", Guid.NewGuid().ToString("N"));
        FileStream? profileLock = null;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            (var profileIndex, profileLock) = AcquireProfile();
            var profileDirectory = Path.Combine(_profileRoot, $"profile-{profileIndex}");
            Directory.CreateDirectory(profileDirectory);

            await RunAsync(soffice, request, profileDirectory, outputDirectory, cancellationToken).ConfigureAwait(false);

            var produced = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(request.SourcePath) + ".pdf");
            if (!File.Exists(produced))
            {
                throw new EngineException(
                    Loc.T("LibreOffice did not produce a PDF. The file may be damaged or protected by a password."));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPdfPath))!);
            File.Move(produced, request.OutputPdfPath, overwrite: true);
        }
        finally
        {
            profileLock?.Dispose();
            _slots.Release();
            TryDeleteDirectory(outputDirectory);
        }
    }

    /// <summary>
    /// Takes a profile folder that no other conversion is using - in this process or in another one.
    /// LibreOffice cannot run two instances on one profile, and two MarkPortO processes (for example two CI jobs)
    /// would otherwise collide. The lock is held by the operating system, so a crashed process frees its profile.
    /// </summary>
    private (int Index, FileStream Lock) AcquireProfile()
    {
        Directory.CreateDirectory(_profileRoot);
        for (var index = 0; index < 256; index++)
        {
            var lockPath = Path.Combine(_profileRoot, $"profile-{index}.lock");
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                return (index, stream);
            }
            catch (IOException)
            {
                // In use by another conversion: try the next one.
            }
        }

        throw new EngineException(Loc.T("No free LibreOffice profile folder was found."));
    }

    public void Dispose() => _slots.Dispose();

    private static async Task RunAsync(
        string soffice,
        PdfExportRequest request,
        string profileDirectory,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(soffice)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[]
        {
            "--headless", "--norestore", "--nolockcheck", "--nodefault", "--nologo", "--nofirststartwizard",
            "-env:UserInstallation=" + new Uri(profileDirectory + Path.DirectorySeparatorChar).AbsoluteUri,
            "--convert-to", "pdf",
            "--outdir", outputDirectory,
            Path.GetFullPath(request.SourcePath),
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new EngineException(Loc.T("LibreOffice could not be started: {0}", ex.Message), ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new EngineException(
                Loc.T("LibreOffice did not finish within {0:0} seconds and was stopped.", request.Timeout.TotalSeconds))
            {
                TimedOut = true,
            };
        }

        if (process.ExitCode != 0)
        {
            string text;
            lock (output)
            {
                text = output.ToString().Trim();
            }

            throw new EngineException(Loc.T("LibreOffice failed (exit code {0}). {1}", process.ExitCode, text).Trim());
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process ended by itself in the meantime.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind in the temp folder; the operating system cleans it up eventually.
        }
    }
}
