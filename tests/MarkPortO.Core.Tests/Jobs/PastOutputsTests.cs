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

using MarkPortO.Core.Jobs;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Jobs;

public class PastOutputsTests
{
    private static (JobState Job, DocumentRecord Document) Save(string outRoot, string folder, string? rel, string name = "report")
    {
        var store = new StateStore(outRoot);
        var document = new DocumentRecord
        {
            DocId = "d0001",
            SourcePath = @"C:\in\" + name + ".docx",
            OutputFolder = folder,
            OutputFolderRel = rel,
            Pages = [new PageRecord { Index = 1, ImageFile = "images/page-001.png" }],
        };
        var job = new JobState { JobId = Guid.NewGuid(), OutputRoot = outRoot, Documents = [document.DocId], UpdatedAt = DateTime.UtcNow };
        Directory.CreateDirectory(store.JobDirectory(job.JobId));
        store.SaveJob(job);
        store.SaveDocument(job.JobId, document);
        return (job, document);
    }

    [Fact]
    public void FindRoot_FindsTheOutputFolderFromItselfAndFromItsChildren()
    {
        using var dir = new TempDir();
        var root = dir.Combine("out");
        var folder = Path.Combine(root, "report");
        Directory.CreateDirectory(folder);
        Save(root, folder, "report");

        Assert.Equal(root, PastOutputs.FindRoot(root));
        Assert.Equal(root, PastOutputs.FindRoot(folder));
        Assert.Equal(root, PastOutputs.FindRoot(Path.Combine(folder, "images")));
    }

    [Fact]
    public void FindRoot_ReturnsNull_WhenThereIsNoStateOrTooFarAbove()
    {
        using var dir = new TempDir();
        var root = dir.Combine("out");
        Directory.CreateDirectory(Path.Combine(root, "a", "b", "c", "d"));
        Save(root, Path.Combine(root, "a"), "a");

        Assert.Null(PastOutputs.FindRoot(dir.Combine("other")));
        Assert.Null(PastOutputs.FindRoot(Path.Combine(root, "a", "b", "c", "d")));
        Assert.Null(PastOutputs.FindRoot(""));
    }

    [Fact]
    public void Load_UsesTheSavedFolder_WhenItExists()
    {
        using var dir = new TempDir();
        var root = dir.Combine("out");
        var folder = Path.Combine(root, "report");
        Directory.CreateDirectory(folder);
        Save(root, folder, "report");

        var found = Assert.Single(PastOutputs.Load(root));
        Assert.True(found.FolderFound);
        Assert.True(found.CanPreview);
        Assert.Equal(folder, found.Document.OutputFolder);
    }

    [Fact]
    public void Load_FindsTheFolderByItsRelativePath_AfterTheOutputWasMoved()
    {
        using var dir = new TempDir();
        var moved = dir.Combine("moved");
        Directory.CreateDirectory(Path.Combine(moved, "sub", "report"));
        Save(moved, dir.Combine("gone", "sub", "report"), Path.Combine("sub", "report"));

        var found = Assert.Single(PastOutputs.Load(moved));
        Assert.True(found.FolderFound);
        Assert.Equal(Path.Combine(moved, "sub", "report"), found.Document.OutputFolder);
    }

    [Fact]
    public void Load_FindsTheFolderByTheTailOfTheSavedPath_ForAStateWithoutTheRelativePath()
    {
        using var dir = new TempDir();
        var moved = dir.Combine("moved");
        Directory.CreateDirectory(Path.Combine(moved, "in", "report (2)"));
        Save(moved, @"D:\old\out\in\report (2)", rel: null);

        var found = Assert.Single(PastOutputs.Load(moved));
        Assert.True(found.FolderFound);
        Assert.Equal(Path.Combine(moved, "in", "report (2)"), found.Document.OutputFolder);
    }

    [Fact]
    public void Load_ReportsAFolderThatIsNotThere_AndDoesNotAllowPreview()
    {
        using var dir = new TempDir();
        var root = dir.Combine("out");
        Directory.CreateDirectory(root);
        Save(root, Path.Combine(root, "report"), "report");

        var found = Assert.Single(PastOutputs.Load(root));
        Assert.False(found.FolderFound);
        Assert.False(found.CanPreview);
    }

    [Fact]
    public void Load_DoesNotFollowARelativePathOutOfTheOutputFolder()
    {
        using var dir = new TempDir();
        var root = dir.Combine("out");
        Directory.CreateDirectory(Path.Combine(dir.Path, "elsewhere"));
        Directory.CreateDirectory(root);
        Save(root, dir.Combine("gone"), Path.Combine("..", "elsewhere"));

        Assert.False(Assert.Single(PastOutputs.Load(root)).FolderFound);
    }

    [Fact]
    public void Load_ListsTheNewestRunFirst()
    {
        using var dir = new TempDir();
        var root = dir.Combine("out");
        Directory.CreateDirectory(Path.Combine(root, "report"));
        Directory.CreateDirectory(Path.Combine(root, "report (2)"));
        var first = Save(root, Path.Combine(root, "report"), "report");
        first.Job.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        new StateStore(root).SaveJob(first.Job, touch: false);
        var second = Save(root, Path.Combine(root, "report (2)"), "report (2)");

        var all = PastOutputs.Load(root);
        Assert.Equal([second.Job.JobId, first.Job.JobId], all.Select(d => d.Job.JobId));
    }
}
