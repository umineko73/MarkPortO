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

namespace MarkPort.Core.Tests.Licensing;

/// <summary>FR-040: every source file carries the GPL-3.0-only header, and the license text is in the repository.</summary>
public class LicenseHeaderTests
{
    private static readonly string[] SkippedFolders = ["bin", "obj", ".git", ".specify", ".claude", ".agents"];

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MarkPort.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("MarkPort.slnx was not found above the test folder.");
    }

    [Fact]
    public void EverySourceFile_HasTheHeader()
    {
        var root = RepositoryRoot();
        var missing = new List<string>();
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (Path.GetExtension(file) is not (".cs" or ".xaml" or ".py")
                || relative.Split(Path.DirectorySeparatorChar).Any(part => SkippedFolders.Contains(part)))
            {
                continue;
            }

            count++;
            var start = File.ReadAllText(file);
            start = start[..Math.Min(start.Length, 1200)];
            if (!start.Contains("SPDX-License-Identifier: GPL-3.0-only"))
            {
                missing.Add(relative);
            }
        }

        Assert.True(count > 100, "the test found the source files");
        Assert.Empty(missing); // run: python tools/add-license-header.py
    }

    [Fact]
    public void TheLicenseText_IsInTheRepository()
    {
        var license = File.ReadAllText(Path.Combine(RepositoryRoot(), "LICENSE"));

        Assert.Contains("GNU GENERAL PUBLIC LICENSE", license);
        Assert.Contains("Version 3, 29 June 2007", license);
    }
}
