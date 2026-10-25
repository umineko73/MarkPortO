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

using System.Security;
using DocumentFormat.OpenXml;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;

namespace MarkPort.Core.Tests.Fixtures;

/// <param name="Title">The title text.</param>
/// <param name="Body">Paragraphs of the body text.</param>
/// <param name="Hidden">The slide is hidden in the slide show.</param>
/// <param name="Table">Optional table; rows of cell texts.</param>
public sealed record SlideSpec(
    string Title,
    string[]? Body = null,
    bool Hidden = false,
    string[][]? Table = null,
    ShapeSpec[]? Shapes = null,
    ShapeGroupSpec? Group = null);

/// <summary>A drawn shape. Position and size are fractions of the slide (or of the group, inside a group).</summary>
/// <param name="Geometry">A preset name such as <c>mathMultiply</c>, <c>line</c>, <c>rect</c>; <c>custGeom</c> for a freehand shape.</param>
public sealed record ShapeSpec(string Geometry, double X, double Y, double Width, double Height, bool FlipH = false, bool FlipV = false)
{
    /// <summary>A "x" made of one multiply shape over the whole slide.</summary>
    public static ShapeSpec[] CrossShape(double size = 0.9) =>
        [new("mathMultiply", (1 - size) / 2, (1 - size) / 2, size, size)];

    /// <summary>A "x" made of two crossing diagonal lines over the whole slide.</summary>
    public static ShapeSpec[] CrossLines(double size = 0.9) =>
    [
        new("line", (1 - size) / 2, (1 - size) / 2, size, size),
        new("line", (1 - size) / 2, (1 - size) / 2, size, size, FlipV: true),
    ];
}

/// <summary>Shapes inside a group shape. The children's fractions refer to the group.</summary>
public sealed record ShapeGroupSpec(double X, double Y, double Width, double Height, ShapeSpec[] Children);

/// <summary>Builds small .pptx files for tests (a master, one layout, and plain slides).</summary>
public static class PptxFactory
{
    private const string NsA = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string NsP = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const int SlideWidth = 9144000;
    private const int SlideHeight = 6858000;

    public static void Create(string path, params SlideSpec[] slides)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentationPart = document.AddPresentationPart();

        var masterPart = presentationPart.AddNewPart<SlideMasterPart>();
        var layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
        var themePart = masterPart.AddNewPart<ThemePart>();
        themePart.Theme = new A.Theme(ThemeXml());
        layoutPart.AddPart(masterPart);
        layoutPart.SlideLayout = new SlideLayout(LayoutXml());
        masterPart.SlideMaster = new SlideMaster(MasterXml(masterPart.GetIdOfPart(layoutPart)));
        presentationPart.AddPart(themePart);

        var slideIds = new StringBuilder();
        uint slideId = 256;
        foreach (var spec in slides)
        {
            var slidePart = presentationPart.AddNewPart<SlidePart>();
            slidePart.AddPart(layoutPart);
            slidePart.Slide = new Slide(SlideXml(spec));
            slideIds.Append(CultureInvariant($"<p:sldId id=\"{slideId++}\" r:id=\"{presentationPart.GetIdOfPart(slidePart)}\"/>"));
        }

        var slideList = slides.Length == 0 ? string.Empty : $"<p:sldIdLst>{slideIds}</p:sldIdLst>";
        presentationPart.Presentation = new Presentation($"""
            <p:presentation xmlns:a="{NsA}" xmlns:r="{NsR}" xmlns:p="{NsP}">
              <p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="{presentationPart.GetIdOfPart(masterPart)}"/></p:sldMasterIdLst>
              {slideList}
              <p:sldSz cx="{SlideWidth}" cy="{SlideHeight}"/>
              <p:notesSz cx="6858000" cy="9144000"/>
            </p:presentation>
            """);
        presentationPart.Presentation.Save();
    }

    private static string CultureInvariant(FormattableString text) => text.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Esc(string text) => SecurityElement.Escape(text) ?? string.Empty;

    private static string GroupHeader() => """
        <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
        <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
        """;

    private static string MasterXml(string layoutRelationshipId) => $"""
        <p:sldMaster xmlns:a="{NsA}" xmlns:r="{NsR}" xmlns:p="{NsP}">
          <p:cSld><p:bg><p:bgRef idx="1001"><a:schemeClr val="bg1"/></p:bgRef></p:bg><p:spTree>{GroupHeader()}</p:spTree></p:cSld>
          <p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>
          <p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="{layoutRelationshipId}"/></p:sldLayoutIdLst>
        </p:sldMaster>
        """;

    private static string LayoutXml() => $"""
        <p:sldLayout xmlns:a="{NsA}" xmlns:r="{NsR}" xmlns:p="{NsP}" type="obj" preserve="1">
          <p:cSld name="Title and Content"><p:spTree>{GroupHeader()}
            {Placeholder(2, "Title 1", "type=\"title\"", 457200, 274638, 8229600, 1143000, string.Empty)}
            {Placeholder(3, "Content 2", "idx=\"1\"", 457200, 1600200, 8229600, 3200000, string.Empty)}
          </p:spTree></p:cSld>
          <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
        </p:sldLayout>
        """;

    private static string SlideXml(SlideSpec spec)
    {
        var show = spec.Hidden ? " show=\"0\"" : string.Empty;
        var title = Placeholder(2, "Title 1", "type=\"title\"", 457200, 274638, 8229600, 1143000, Paragraph(spec.Title));
        var body = spec.Body is { Length: > 0 }
            ? Placeholder(3, "Content 2", "idx=\"1\"", 457200, 1600200, 8229600, 1300000, string.Concat(spec.Body.Select(Paragraph)))
            : string.Empty;
        var table = spec.Table is { Length: > 0 } ? Table(spec.Table) : string.Empty;
        var shapes = spec.Shapes is { Length: > 0 }
            ? string.Concat(spec.Shapes.Select((shape, i) => ShapeXml(shape, 20 + i, SlideWidth, SlideHeight)))
            : string.Empty;
        var group = spec.Group is { } g ? GroupXml(g) : string.Empty;

        return $"""
            <p:sld xmlns:a="{NsA}" xmlns:r="{NsR}" xmlns:p="{NsP}"{show}>
              <p:cSld><p:spTree>{GroupHeader()}{title}{body}{table}{shapes}{group}</p:spTree></p:cSld>
              <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
            </p:sld>
            """;
    }

    private static string ShapeXml(ShapeSpec shape, int id, double width, double height)
    {
        var flips = (shape.FlipH ? " flipH=\"1\"" : string.Empty) + (shape.FlipV ? " flipV=\"1\"" : string.Empty);
        var geometry = shape.Geometry == "custGeom"
            ? "<a:custGeom><a:avLst/><a:gdLst/><a:ahLst/><a:cxnLst/><a:rect l=\"0\" t=\"0\" r=\"r\" b=\"b\"/><a:pathLst><a:path w=\"100\" h=\"100\"><a:moveTo><a:pt x=\"0\" y=\"0\"/></a:moveTo><a:lnTo><a:pt x=\"100\" y=\"100\"/></a:lnTo></a:path></a:pathLst></a:custGeom>"
            : $"<a:prstGeom prst=\"{shape.Geometry}\"><a:avLst/></a:prstGeom>";
        var xfrm = CultureInvariant($"<a:xfrm{flips}><a:off x=\"{(long)(shape.X * width)}\" y=\"{(long)(shape.Y * height)}\"/><a:ext cx=\"{(long)(shape.Width * width)}\" cy=\"{(long)(shape.Height * height)}\"/></a:xfrm>");
        var line = "<a:ln w=\"38100\"><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></a:ln>";

        return shape.Geometry is "line" or "straightConnector1"
            ? CultureInvariant($"<p:cxnSp><p:nvCxnSpPr><p:cNvPr id=\"{id}\" name=\"Line {id}\"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr><p:spPr>{xfrm}{geometry}{line}</p:spPr></p:cxnSp>")
            : CultureInvariant($"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Shape {id}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr>{xfrm}{geometry}{line}</p:spPr></p:sp>");
    }

    private static string GroupXml(ShapeGroupSpec group)
    {
        var width = (long)(group.Width * SlideWidth);
        var height = (long)(group.Height * SlideHeight);
        var children = string.Concat(group.Children.Select((c, i) => ShapeXml(c, 40 + i, width, height)));
        return CultureInvariant($"""
            <p:grpSp><p:nvGrpSpPr><p:cNvPr id="30" name="Group 30"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
            <p:grpSpPr><a:xfrm><a:off x="{(long)(group.X * SlideWidth)}" y="{(long)(group.Y * SlideHeight)}"/><a:ext cx="{width}" cy="{height}"/><a:chOff x="0" y="0"/><a:chExt cx="{width}" cy="{height}"/></a:xfrm></p:grpSpPr>
            {children}</p:grpSp>
            """);
    }

    private static string Paragraph(string text) =>
        $"<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>{Esc(text)}</a:t></a:r></a:p>";

    private static string Placeholder(int id, string name, string phAttributes, int x, int y, int cx, int cy, string paragraphs)
    {
        var body = paragraphs.Length == 0 ? "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>" : paragraphs;
        return CultureInvariant($"""
            <p:sp><p:nvSpPr><p:cNvPr id="{id}" name="{name}"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph {phAttributes}/></p:nvPr></p:nvSpPr>
            <p:spPr><a:xfrm><a:off x="{x}" y="{y}"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm></p:spPr>
            <p:txBody><a:bodyPr/><a:lstStyle/>{body}</p:txBody></p:sp>
            """);
    }

    private static string Table(string[][] rows)
    {
        var columns = rows.Max(r => r.Length);
        const int totalWidth = 8229600;
        var columnWidth = totalWidth / columns;
        var grid = string.Concat(Enumerable.Repeat(CultureInvariant($"<a:gridCol w=\"{columnWidth}\"/>"), columns));
        var rowXml = string.Concat(rows.Select(row =>
            "<a:tr h=\"370840\">"
            + string.Concat(row.Select(cell =>
                $"<a:tc><a:txBody><a:bodyPr/><a:lstStyle/>{Paragraph(cell)}</a:txBody><a:tcPr/></a:tc>"))
            + "</a:tr>"));

        return CultureInvariant($"""
            <p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id="4" name="Table 3"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp="1"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>
            <p:xfrm><a:off x="457200" y="3200000"/><a:ext cx="{totalWidth}" cy="{370840 * rows.Length}"/></p:xfrm>
            <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/table"><a:tbl><a:tblPr firstRow="1"/><a:tblGrid>{grid}</a:tblGrid>{rowXml}</a:tbl></a:graphicData></a:graphic></p:graphicFrame>
            """);
    }

    private static string ThemeXml()
    {
        const string fill = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        const string line = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        return $"""
            <a:theme xmlns:a="{NsA}" name="Office Theme"><a:themeElements>
            <a:clrScheme name="Office">
              <a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
              <a:dk2><a:srgbClr val="44546A"/></a:dk2><a:lt2><a:srgbClr val="E7E6E6"/></a:lt2>
              <a:accent1><a:srgbClr val="4472C4"/></a:accent1><a:accent2><a:srgbClr val="ED7D31"/></a:accent2>
              <a:accent3><a:srgbClr val="A5A5A5"/></a:accent3><a:accent4><a:srgbClr val="FFC000"/></a:accent4>
              <a:accent5><a:srgbClr val="5B9BD5"/></a:accent5><a:accent6><a:srgbClr val="70AD47"/></a:accent6>
              <a:hlink><a:srgbClr val="0563C1"/></a:hlink><a:folHlink><a:srgbClr val="954F72"/></a:folHlink>
            </a:clrScheme>
            <a:fontScheme name="Office">
              <a:majorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont>
              <a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont>
            </a:fontScheme>
            <a:fmtScheme name="Office">
              <a:fillStyleLst>{fill}{fill}{fill}</a:fillStyleLst>
              <a:lnStyleLst><a:ln w="9525">{line}</a:ln><a:ln w="19050">{line}</a:ln><a:ln w="28575">{line}</a:ln></a:lnStyleLst>
              <a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>
              <a:bgFillStyleLst>{fill}{fill}{fill}</a:bgFillStyleLst>
            </a:fmtScheme>
            </a:themeElements></a:theme>
            """;
    }
}
