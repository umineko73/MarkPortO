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

using System.Text.RegularExpressions;

namespace MarkPort.Core.Tests.Naming;

/// <summary>
/// FR-038, SC-021: the conversion steps are called Step1 to Step3 and the image-reading model is called VLM
/// everywhere (screen, command line, settings, Markdown, source). The old words must not come back.
/// </summary>
public partial class TerminologyTests
{
    private static readonly string[] SkippedFolders = ["bin", "obj", ".git", ".specify", ".claude", ".agents"];

    [GeneratedRegex(@"stage|phase|段階|(?-i:(?<![A-Za-z])AI(?![A-Za-z]))", RegexOptions.IgnoreCase)]
    private static partial Regex OldWords();

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
    public void TheOldWords_AreNotUsed()
    {
        var root = RepositoryRoot();
        var found = new List<string>();
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            var inScope = (parts[0] is "src" or "tests" && Path.GetExtension(file) is ".cs" or ".xaml")
                          || relative == "README.md"
                          || (parts[0] == "specs" && parts.Contains("contracts"));
            if (!inScope || parts.Any(part => SkippedFolders.Contains(part)) || Path.GetFileName(file) == "TerminologyTests.cs")
            {
                continue;
            }

            count++;
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (OldWords().IsMatch(line))
                {
                    found.Add($"{relative}:{lineNumber}: {line.Trim()}");
                }
            }
        }

        Assert.True(count > 100, "the test found the files");
        Assert.Empty(found);
    }
}
