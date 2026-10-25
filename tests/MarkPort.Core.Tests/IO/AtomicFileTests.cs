// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
// Author: umineko73
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
using MarkPort.Core.Tests.Fixtures;

namespace MarkPort.Core.Tests.IO;

public class AtomicFileTests
{
    [Fact]
    public void WriteAllText_CreatesFileWithoutBom()
    {
        using var dir = new TempDir();
        var path = dir.Combine("sub", "a.txt");

        AtomicFile.WriteAllText(path, "こんにちは");

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]); // no UTF-8 BOM
        Assert.Equal("こんにちは", Encoding.UTF8.GetString(bytes));
        Assert.Empty(Directory.GetFiles(dir.Path, "*" + AtomicFile.TempSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public void WriteAllText_ReplacesExistingFile()
    {
        using var dir = new TempDir();
        var path = dir.Combine("a.txt");
        File.WriteAllText(path, "old");

        AtomicFile.WriteAllText(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void Write_WhenWriterFails_KeepsOriginalAndLeavesNoTemp()
    {
        using var dir = new TempDir();
        var path = dir.Combine("a.txt");
        File.WriteAllText(path, "original");

        Assert.Throws<InvalidOperationException>(() =>
            AtomicFile.Write(path, stream =>
            {
                stream.WriteByte(1);
                throw new InvalidOperationException("boom");
            }));

        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*" + AtomicFile.TempSuffix));
    }

    [Fact]
    public void Write_WhenWriterFailsAndNoOriginal_CreatesNothing()
    {
        using var dir = new TempDir();
        var path = dir.Combine("new.txt");

        Assert.Throws<InvalidOperationException>(() =>
            AtomicFile.Write(path, _ => throw new InvalidOperationException("boom")));

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void DeleteStaleTemps_RemovesOnlyTempFiles()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("keep.md"), "x");
        File.WriteAllText(dir.Combine("a.md" + AtomicFile.TempSuffix), "x");
        Directory.CreateDirectory(dir.Combine("images"));
        File.WriteAllText(dir.Combine("images", "p.png" + AtomicFile.TempSuffix), "x");

        var removed = AtomicFile.DeleteStaleTemps(dir.Path);

        Assert.Equal(2, removed);
        Assert.True(File.Exists(dir.Combine("keep.md")));
    }
}
