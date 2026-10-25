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
using System.Text.Json;
using MarkPort.Core.Jobs.Models;

namespace MarkPort.Core.Tests.Jobs;

public class ModelSerializationTests
{
    [Fact]
    public void DocumentRecord_UsesCamelCaseNamesAndPascalCaseStepStatus()
    {
        var record = new DocumentRecord
        {
            DocId = "d0001",
            Kind = DocumentKind.Docx,
            Step1 = StepStatus.Done,
            Step2 = StepStatus.NotApplicable,
            Step3 = StepStatus.Interrupted,
        };

        var json = JsonDefaults.Serialize(record);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("d0001", root.GetProperty("docId").GetString());
        Assert.Equal("docx", root.GetProperty("kind").GetString());
        Assert.Equal("Done", root.GetProperty("step1").GetString());
        Assert.Equal("NotApplicable", root.GetProperty("step2").GetString());
        Assert.Equal("Interrupted", root.GetProperty("step3").GetString());
    }

    [Fact]
    public void AllStepStatusValuesRoundTrip()
    {
        foreach (var status in Enum.GetValues<StepStatus>())
        {
            var record = new DocumentRecord { Step1 = status };
            var back = JsonDefaults.Deserialize<DocumentRecord>(JsonDefaults.Serialize(record))!;
            Assert.Equal(status, back.Step1);
        }

        Assert.Equal(
            ["NotApplicable", "Pending", "Running", "Done", "Failed", "Interrupted"],
            Enum.GetNames<StepStatus>());
    }

    [Fact]
    public void UsageInfo_NullValuesAreOmittedAndRoundTripAsNull()
    {
        var page = new PageRecord { Index = 1, Usage = UsageInfo.Unavailable };

        var json = JsonDefaults.Serialize(page);
        var back = JsonDefaults.Deserialize<PageRecord>(json)!;

        Assert.DoesNotContain("promptTokens", json);
        Assert.NotNull(back.Usage);
        Assert.False(back.Usage!.IsAvailable);
        Assert.Null(back.Usage.TotalTokens);
    }

    [Fact]
    public void JobState_RoundTripsWithAllFields()
    {
        var state = new JobState
        {
            JobId = Guid.NewGuid(),
            CreatedAt = new DateTime(2026, 10, 25, 1, 2, 3, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 10, 25, 1, 5, 0, DateTimeKind.Utc),
            Status = JobStatus.Interrupted,
            Inputs = ["D:\\in\\a.docx"],
            InputRoot = string.Empty,
            OutputRoot = "D:\\out",
            Options = new OptionsSnapshot
            {
                Types = [DocumentKind.Docx, DocumentKind.Xlsx],
                Recurse = true,
                Dpi = 150,
                Engine = EngineKind.LibreOffice,
                Step2 = true,
            },
            Documents = ["d0001", "d0002"],
        };

        var back = JsonDefaults.Deserialize<JobState>(JsonDefaults.Serialize(state))!;

        Assert.Equal(state.JobId, back.JobId);
        Assert.Equal(JobStatus.Interrupted, back.Status);
        Assert.Equal(state.Inputs, back.Inputs);
        Assert.Equal(EngineKind.LibreOffice, back.Options.Engine);
        Assert.Equal(state.Options.Types, back.Options.Types);
        Assert.Equal(state.Documents, back.Documents);
        Assert.Equal(state.UpdatedAt, back.UpdatedAt);
    }

    [Fact]
    public void SerializeToUtf8_HasNoByteOrderMark()
    {
        var bytes = JsonDefaults.SerializeToUtf8(new Warning("PageUnresolved", "日本語"));

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("日本語", Encoding.UTF8.GetString(bytes));
    }
}
