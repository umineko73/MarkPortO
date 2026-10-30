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

using System.IO.Compression;
using System.Text;
using MarkPortO.Core.Engines;
using MarkPortO.Core.Ooxml;
using MarkPortO.Core.Tests.Fixtures;

namespace MarkPortO.Core.Tests.Docx;

/// <summary>The numbers of figures and tables (<c>SEQ</c> fields) are the ones that Word counts, not the old ones saved in the file.</summary>
public class FieldUpdateTests
{
    private const string Caption =
        "<w:p><w:r><w:t xml:space=\"preserve\">Figure </w:t></w:r>" +
        "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> SEQ Figure \\* ARABIC </w:instrText></w:r>" +
        "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>{0}</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>" +
        "<w:r><w:t xml:space=\"preserve\"> caption</w:t></w:r></w:p>";

    /// <summary>A docx with figure captions whose saved numbers are the given ones.</summary>
    private static string Create(TempDir dir, string name, params int[] saved)
    {
        var path = dir.Combine(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string entry, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        Add("word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
            string.Concat(saved.Select(n => string.Format(Caption, n))) + "<w:sectPr/></w:body></w:document>");
        return path;
    }

    [Fact]
    public void ADocumentWithAFigureNumberField_IsFound()
    {
        using var dir = new TempDir();

        Assert.True(DocxFieldProbe.HasCountingFields(Create(dir, "a.docx", 4, 5)));
    }

    [Fact]
    public void ADocumentWithoutSuchFields_IsNotFound()
    {
        using var dir = new TempDir();
        var path = dir.Combine("plain.docx");
        DocxFactory.CreatePaged(path, ["text"]);

        Assert.False(DocxFieldProbe.HasCountingFields(path));
    }

    [Theory]
    [InlineData("missing.docx")]
    public void AFileThatIsNotThere_IsNotFound(string name)
    {
        using var dir = new TempDir();

        Assert.False(DocxFieldProbe.HasCountingFields(dir.Combine(name)));
    }

    [OfficeFact]
    public async Task Word_CountsTheNumbersAgain_InTheCopy()
    {
        using var dir = new TempDir();
        var source = Create(dir, "stale.docx", 4, 5, 6); // the numbers saved are old: Word counts 1, 2, 3
        var copy = dir.Combine("copy", "updated.docx");

        using var engine = new OfficeEngine();
        var made = await engine.TryUpdateFieldsAsync(source, copy, PdfExportRequest.DefaultTimeout, CancellationToken.None);

        Assert.True(made);
        var text = string.Concat(DocxReader.Read(copy).Select(b => b.SearchText + "|"));
        Assert.Contains("Figure 1 caption", text);
        Assert.Contains("Figure 3 caption", text);
        Assert.DoesNotContain("Figure 4 caption", text);
        // the file that was given is not changed
        Assert.Contains("Figure 4 caption", string.Concat(DocxReader.Read(source).Select(b => b.SearchText + "|")));
    }
}
