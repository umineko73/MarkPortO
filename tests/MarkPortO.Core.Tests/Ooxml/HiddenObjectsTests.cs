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
