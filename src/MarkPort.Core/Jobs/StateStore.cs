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

using System.Text;
using MarkPort.Core.IO;
using MarkPort.Core.Jobs.Models;

namespace MarkPort.Core.Jobs;

/// <summary>
/// Keeps the state needed to resume a run under <c>&lt;--out&gt;/.markport/jobs/&lt;jobId&gt;/</c> (Decision 003).
/// Every write is atomic, so an interrupted run never leaves a half-written state file.
/// </summary>
public sealed class StateStore
{
    public const string StateFolderName = ".markport";
    private const string JobFileName = "job.json";
    private const string LockFileName = "lock";

    private readonly string _jobsRoot;

    public StateStore(string outRoot)
    {
        OutRoot = Path.GetFullPath(outRoot);
        _jobsRoot = Path.Combine(OutRoot, StateFolderName, "jobs");
    }

    public string OutRoot { get; }

    public string JobDirectory(Guid jobId) => Path.Combine(_jobsRoot, jobId.ToString("D"));

    /// <summary>Scratch space for one document (temporary copies, the PDF). Removed after the document is processed.</summary>
    public string WorkDirectory(Guid jobId, string docId) => Path.Combine(JobDirectory(jobId), "work", docId);

    public void SaveJob(JobState job, bool touch = true)
    {
        if (touch)
        {
            job.UpdatedAt = DateTime.UtcNow;
        }

        AtomicFile.WriteAllBytes(Path.Combine(JobDirectory(job.JobId), JobFileName), JsonDefaults.SerializeToUtf8(job));
    }

    public JobState? LoadJob(Guid jobId) => ReadJson<JobState>(Path.Combine(JobDirectory(jobId), JobFileName));

    public void SaveDocument(Guid jobId, DocumentRecord document) =>
        AtomicFile.WriteAllBytes(DocumentPath(jobId, document.DocId), JsonDefaults.SerializeToUtf8(document));

    public DocumentRecord? LoadDocument(Guid jobId, string docId) =>
        ReadJson<DocumentRecord>(DocumentPath(jobId, docId));

    /// <summary>All jobs under the output folder, newest first.</summary>
    public IReadOnlyList<JobState> ListJobs()
    {
        if (!Directory.Exists(_jobsRoot))
        {
            return [];
        }

        var jobs = new List<JobState>();
        foreach (var directory in Directory.EnumerateDirectories(_jobsRoot))
        {
            if (!Guid.TryParse(Path.GetFileName(directory), out var id))
            {
                continue;
            }

            var job = LoadJob(id);
            if (job is not null)
            {
                jobs.Add(job);
            }
        }

        return [.. jobs.OrderByDescending(j => j.UpdatedAt)];
    }

    /// <summary>
    /// Jobs that can be resumed: interrupted ones, and ones left as "running" whose process is gone
    /// (crash or power loss). If <paramref name="inputs"/> is given, only jobs started with the same inputs.
    /// Newest first. With <paramref name="includeFinishedWithErrors"/>, finished jobs that have failed files
    /// are included too (for "retry failed").
    /// </summary>
    public IReadOnlyList<JobState> FindResumableJobs(
        IReadOnlyCollection<string>? inputs = null,
        bool includeFinishedWithErrors = false)
    {
        var wanted = inputs is { Count: > 0 } ? NormalizeInputs(inputs) : null;

        return
        [
            .. ListJobs().Where(job =>
                IsResumable(job, includeFinishedWithErrors)
                && (wanted is null || NormalizeInputs(job.Inputs).SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase))),
        ];
    }

    public bool IsResumable(JobState job, bool includeFinishedWithErrors = false) => job.Status switch
    {
        JobStatus.Interrupted => !IsLocked(job.JobId),
        JobStatus.Running => !IsLocked(job.JobId), // the process that was running it is gone
        JobStatus.CompletedWithErrors => includeFinishedWithErrors && !IsLocked(job.JobId),
        _ => false,
    };

    /// <summary>
    /// Takes the lock of a job so no second process runs it. The lock is held by the operating system:
    /// it is released if the process dies, so a crashed run never blocks a resume.
    /// </summary>
    public JobLock? TryAcquireLock(Guid jobId)
    {
        var path = Path.Combine(JobDirectory(jobId), LockFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var stream = new FileStream(
                path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var text = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            stream.SetLength(0);
            stream.Write(text, 0, text.Length);
            stream.Flush();
            return new JobLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public bool IsLocked(Guid jobId)
    {
        var path = Path.Combine(JobDirectory(jobId), LockFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public static Fingerprint ComputeFingerprint(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The input file was not found.", path);
        }

        return new Fingerprint(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    public static bool HasChanged(Fingerprint saved, string path)
    {
        if (!File.Exists(path))
        {
            return true;
        }

        return ComputeFingerprint(path) != saved;
    }

    /// <summary>Absolute, de-duplicated and sorted, so two lists of the same inputs compare equal.</summary>
    public static IReadOnlyList<string> NormalizeInputs(IEnumerable<string> inputs) =>
        [
            .. inputs
                .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
        ];

    private string DocumentPath(Guid jobId, string docId) =>
        Path.Combine(JobDirectory(jobId), "docs", docId + ".json");

    private static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonDefaults.Deserialize<T>(File.ReadAllText(path, AtomicFile.Utf8NoBom));
    }
}

/// <summary>Holds the lock of a job until disposed.</summary>
public sealed class JobLock(FileStream stream) : IDisposable
{
    public void Dispose() => stream.Dispose();
}
