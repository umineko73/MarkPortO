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

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Ooxml;

public class HiddenObjectsTests
{
    [Fact]
    public void Pptx_AHiddenShapeIsNotWritten_AndAShownOneIs()
    {
        using var dir = new TempDir();
        var path = dir.Combine("d.pptx");
        PptxFactory.Create(path, new SlideSpec("Kept title", ["secret-body"]));
        Assert.Contains("secret-body", Assert.Single(PptxReader.Read(path, includeHidden: false)).Markdown, StringComparison.Ordinal);

        using (var document = PresentationDocument.Open(path, true))
        {
            var slide = document.PresentationPart!.SlideParts.Single().Slide!;
            var shape = slide.Descendants<DocumentFormat.OpenXml.Presentation.Shape>().Single(s => s.InnerText.Contains("secret-body", StringComparison.Ordinal));
            shape.NonVisualShapeProperties!.NonVisualDrawingProperties!.Hidden = true;
            slide.Save();
        }

        var markdown = Assert.Single(PptxReader.Read(path, includeHidden: false)).Markdown;
        Assert.DoesNotContain("secret-body", markdown, StringComparison.Ordinal);
        Assert.Contains("Kept title", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Pptx_AHiddenChartIsNotWritten_AndAShownOneIs(bool hidden, bool written)
    {
        using var dir = new TempDir();
        var path = dir.Combine("c.pptx");
        PptxFactory.Create(path, new SlideSpec("Charts", ["intro"]));
        using (var document = PresentationDocument.Open(path, true))
        {
            var slidePart = document.PresentationPart!.SlideParts.Single();
            var chart = slidePart.AddNewPart<ChartPart>();
            chart.ChartSpace = new DocumentFormat.OpenXml.Drawing.Charts.ChartSpace(XlsxReaderTests.BarChartXml);
            var id = slidePart.GetIdOfPart(chart);
            string xml;
            using (var reader = new StreamReader(slidePart.GetStream()))
            {
                xml = reader.ReadToEnd();
            }

            var frame = $"""
                <p:graphicFrame xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:nvGraphicFramePr><p:cNvPr id="50" name="Chart 1" hidden="{(hidden ? 1 : 0)}"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>
                  <p:xfrm><a:off x="0" y="0"/><a:ext cx="100" cy="100"/></p:xfrm>
                  <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/chart"><c:chart xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" r:id="{id}"/></a:graphicData></a:graphic>
                </p:graphicFrame>
                """;
            xml = xml.Replace("</p:spTree>", frame + "</p:spTree>", StringComparison.Ordinal);
            using var writer = new StreamWriter(slidePart.GetStream(FileMode.Create));
            writer.Write(xml);
        }

        var markdown = Assert.Single(PptxReader.Read(path, includeHidden: false)).Markdown;

        Assert.Equal(written, markdown.Contains("Sales by year", StringComparison.Ordinal));
    }

    [Fact]
    public void IsHidden_ReadsTheHiddenFlagOfEveryKindOfObject()
    {
        static OpenXmlElement Parse(string xml) => new DocumentFormat.OpenXml.Presentation.Picture(xml);

        const string Ns = "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
        Assert.True(HiddenObjects.IsHidden(Parse($"<p:pic {Ns}><p:nvPicPr><p:cNvPr id=\"2\" name=\"x\" hidden=\"1\"/></p:nvPicPr></p:pic>")));
        Assert.False(HiddenObjects.IsHidden(Parse($"<p:pic {Ns}><p:nvPicPr><p:cNvPr id=\"2\" name=\"x\" hidden=\"0\"/></p:nvPicPr></p:pic>")));
        Assert.False(HiddenObjects.IsHidden(Parse($"<p:pic {Ns}><p:nvPicPr><p:cNvPr id=\"2\" name=\"x\"/></p:nvPicPr></p:pic>")));
    }
}
