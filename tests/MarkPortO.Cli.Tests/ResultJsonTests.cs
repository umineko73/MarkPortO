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

using System.Text.Json;
using MarkPortO.Core.Logging;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Cli.Tests;

[Trait("Engine", "LibreOffice")]
public class ResultJsonTests
{
    [LibreOfficeFact]
    public async Task TheResultFile_HasTheDocumentedShape_ForSuccessAndFailure()
    {
        using var dir = new TempDir();
        var good = dir.Combine("in", "good.docx");
        DocxFactory.CreatePaged(good, ["page one"], ["page two"]);
        var broken = dir.Combine("in", "broken.docx");
        DocxFactory.CreateCorrupt(broken);
        var resultPath = dir.Combine("result", "result.json");

        var run = await Cli.RunAsync("convert", good, broken, "--out", dir.Combine("out"), "--engine", "libreoffice", "--dpi", "72", "--result", resultPath);

        Assert.Equal(ExitCodes.SomeFailed, run.ExitCode);
        using var json = JsonDocument.Parse(File.ReadAllText(resultPath));
        var root = json.RootElement;
        Assert.True(Guid.TryParse(root.GetProperty("jobId").GetString(), out _));
        Assert.Equal("CompletedWithErrors", root.GetProperty("status").GetString());
        Assert.Equal(dir.Combine("out"), root.GetProperty("outputRoot").GetString());

        var documents = root.GetProperty("documents").EnumerateArray().ToDictionary(d => Path.GetFileName(d.GetProperty("source").GetString())!);
        var ok = documents["good.docx"];
        Assert.Equal("Done", ok.GetProperty("state").GetString());
        Assert.Equal(2, ok.GetProperty("pages").GetInt32());
        Assert.Equal(0, ok.GetProperty("deletedPages").GetArrayLength());
        Assert.Equal(dir.Combine("out", "good"), ok.GetProperty("outputFolder").GetString());
        Assert.False(ok.TryGetProperty("error", out _));
        Assert.False(ok.TryGetProperty("usage", out _)); // step 3 was not run

        var failed = documents["broken.docx"];
        Assert.Equal("Failed", failed.GetProperty("state").GetString());
        Assert.Equal(1, failed.GetProperty("error").GetProperty("step").GetInt32());
        Assert.Contains("cannot be opened", failed.GetProperty("error").GetProperty("message").GetString());
    }

    [LibreOfficeFact]
    public async Task TheResultFile_DoesNotContainASecret()
    {
        using var dir = new TempDir();
        var good = dir.Combine("in", "good.docx");
        DocxFactory.CreatePaged(good, ["page one"]);
        var resultPath = dir.Combine("result.json");
        SecretRegistry.Shared.Register("sk-never-in-result-9876");

        await Cli.RunAsync("convert", good, "--out", dir.Combine("out"), "--engine", "libreoffice", "--dpi", "72", "--result", resultPath);

        var text = File.ReadAllText(resultPath);
        Assert.DoesNotContain("sk-never-in-result-9876", text);
        Assert.DoesNotContain("apiKey", text, StringComparison.OrdinalIgnoreCase);
    }

    [LibreOfficeFact]
    public async Task TheResultFile_IsWrittenInUtf8WithoutBom_AndReadable()
    {
        using var dir = new TempDir();
        var good = dir.Combine("in", "報告書.docx");
        DocxFactory.CreatePaged(good, ["page one"]);
        var resultPath = dir.Combine("result.json");

        await Cli.RunAsync("convert", good, "--out", dir.Combine("出力"), "--engine", "libreoffice", "--dpi", "72", "--result", resultPath);

        var bytes = File.ReadAllBytes(resultPath);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("報告書.docx", File.ReadAllText(resultPath));
    }
}
