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

using DocumentFormat.OpenXml.Packaging;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Ooxml;

public class PptxMathTests
{
    // How PowerPoint saves an equation: a shape in AlternateContent (the Choice), with the picture of it as the Fallback.
    private const string Equation = """
        <mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006">
          <mc:Choice xmlns:a14="http://schemas.microsoft.com/office/drawing/2010/main" Requires="a14">
            <p:sp xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <p:nvSpPr><p:cNvPr id="40" name="Equation"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr>
              <p:spPr><a:xfrm><a:off x="914400" y="914400"/><a:ext cx="3000000" cy="600000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
              <p:txBody><a:bodyPr/><a:lstStyle/>
                <a:p><a14:m><m:oMathPara xmlns:m="http://schemas.openxmlformats.org/officeDocument/2006/math"><m:oMath>
                  <m:r><m:t>y=</m:t></m:r><m:f><m:num><m:r><m:t>a</m:t></m:r></m:num><m:den><m:r><m:t>b</m:t></m:r></m:den></m:f><m:sSup><m:e><m:r><m:t>x</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup>
                </m:oMath></m:oMathPara></a14:m></a:p>
              </p:txBody>
            </p:sp>
          </mc:Choice>
          <mc:Fallback>
            <p:pic xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:nvPicPr><p:cNvPr id="41" name="Picture of the equation" descr="fallback-picture"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill/><p:spPr/></p:pic>
          </mc:Fallback>
        </mc:AlternateContent>
        """;

    [Fact]
    public void AnEquationOnASlide_IsWrittenAsALineOfText_NotItsFallbackPicture()
    {
        using var dir = new TempDir();
        var path = dir.Combine("d.pptx");
        PptxFactory.Create(path, new SlideSpec("Equations", ["intro"]));
        using (var document = PresentationDocument.Open(path, true))
        {
            var part = document.PresentationPart!.SlideParts.Single();
            string xml;
            using (var reader = new StreamReader(part.GetStream()))
            {
                xml = reader.ReadToEnd();
            }

            xml = xml.Replace("</p:spTree>", Equation + "</p:spTree>", StringComparison.Ordinal);
            using var writer = new StreamWriter(part.GetStream(FileMode.Create));
            writer.Write(xml);
        }

        var markdown = Assert.Single(PptxReader.Read(path, includeHidden: false)).Markdown;

        Assert.Contains("y=a/bx^2", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("fallback-picture", markdown, StringComparison.Ordinal);
    }
}
