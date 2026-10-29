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

using MarkPortO.Core.Localization;
using System.Globalization;
using System.Text;
using MarkPortO.Core.IO;
using MarkPortO.Core.Jobs.Models;
using MarkPortO.Core.Logging;
using MarkPortO.Core.Ooxml;

namespace MarkPortO.Core.Markdown;

/// <summary>
/// Builds the Markdown of one document from its model, following <c>contracts/markdown-format.md</c>.
/// The whole file is built again after each step, so a stopped run still gives a consistent result (research R13).
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>Digits of the page number in image names: at least three (<c>page-001.png</c>).</summary>
    public const int MinimumImageDigits = 3;

    /// <summary>The Markdown of the document. The VLM results are in it unless they are written to a separate file (FR-058).</summary>
    public static string Render(DocumentRecord document) => Render(document, vlmFile: false);

    /// <summary>The separate file of the VLM results (FR-058): the page separators, the results, the usage, the failed and deleted pages.</summary>
    public static string RenderVlm(DocumentRecord document) => Render(document, vlmFile: true);

    private static string Render(DocumentRecord document, bool vlmFile)
    {
        var includeImages = document.Step2 == StepStatus.Done && !vlmFile;
        var hasVlm = document.Step3 != StepStatus.NotApplicable;
        var includeVlm = hasVlm && (vlmFile || !document.VlmSeparate);
        var fileName = Path.GetFileName(document.SourcePath);
        var language = document.MarkdownLanguage;
        var name = Path.GetFileNameWithoutExtension(document.SourcePath);

        var builder = new StringBuilder();
        builder.Append("# ").Append(MarkdownText.Escape(fileName).Replace("\n", " ", StringComparison.Ordinal)).Append("\n\n");
        AppendStructureNote(builder, fileName, document.Kind, includeImages, includeVlm, document.Pages.Any(p => p.Bands > 1),
            document.Blocks.Any(b => MarkdownText.HasMarks(b.Markdown)), document.Blocks.Any(b => b.JoinedToPage is not null), language,
            vlmFile ? MarkdownFileName(name) : null, !vlmFile && hasVlm && document.VlmSeparate ? VlmFileName(name) : null);

        if (includeVlm && TryUsageLine(document.Pages, language) is { } usageLine)
        {
            builder.Append('\n').Append(usageLine).Append('\n');
            if (document.Pages.Where(p => !p.IsDeleted && p.CostUsd.HasValue).Sum(p => p.CostUsd) is > 0 and var total)
            {
                builder.Append("> ").Append(Loc.M("**VLM cost**: {0} US dollars in total", language, total.ToString("0.####", CultureInfo.InvariantCulture))).Append('\n');
            }
        }

        var effectivePages = DocxPageAssigner.EffectivePages(document.Blocks, document.Pages.Count);
        var blockTexts = JoinTableParts(document.Blocks, effectivePages, language);
        var vlmTexts = includeVlm ? VlmTableJoiner.Join(document, effectivePages, language) : new Dictionary<int, string>();
        foreach (var page in document.Pages.OrderBy(p => p.Index))
        {
            builder.Append('\n');
            AppendPage(builder, document, page, effectivePages, blockTexts, vlmTexts, includeImages, includeVlm, vlmFile, language);
        }

        return builder.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>The name of the image file of a page: <c>images/page-003.png</c>.</summary>
    public static string ImageFileName(int pageIndex, int pageCount)
    {
        var digits = Math.Max(MinimumImageDigits, pageCount.ToString(CultureInfo.InvariantCulture).Length);
        return "images/page-" + pageIndex.ToString("D" + digits, CultureInfo.InvariantCulture) + ".png";
    }

    private static void AppendStructureNote(StringBuilder builder, string fileName, DocumentKind kind, bool includeImages, bool includeVlm, bool hasBands, bool hasMergedCells, bool hasJoinedTables, string language, string? mainFile, string? vlmFile)
    {
        void Line(string text) => builder.Append("> ").Append(text).Append('\n');

        Line(Loc.M("**About this Markdown**", language));
        builder.Append(">\n");
        Line(Loc.M("- Source file: `{0}`", language, fileName.Replace("`", string.Empty, StringComparison.Ordinal)));
        Line(Loc.M("- Page separator: `<!-- page: N -->` (N is the page number). Everything from this line to the next separator is the content of that page.", language));
        Line(includeImages
            ? Loc.M("- Page images (Step2): `![Page N](images/page-NNN.png)`. An image of the page as it is printed.", language)
            : Loc.M("- Page images (Step2): not included in this Markdown. The image files are saved in `images/`.", language));
        Line(includeVlm
            ? Loc.M("- VLM results (Step3): between `<!-- vlm:begin page=N -->` and `<!-- vlm:end -->`. The text read from the image. The usage is written in `<!-- usage page=N prompt=… completion=… total=… -->`.", language)
            : vlmFile is not null
                ? Loc.M("- VLM results (Step3): not included in this Markdown. They are in `{0}`.", language, vlmFile)
                : Loc.M("- VLM results (Step3): not included in this Markdown.", language));
        if (mainFile is not null)
        {
            Line(Loc.M("- The text of the document is in `{0}`. This file has only the results of Step3.", language, mainFile));
        }

        if (includeVlm && hasBands)
        {
            Line(Loc.M("- Pages read in bands: `<!-- vlm:begin page=N bands=M -->`. A page with many lines was read in M parts, from the top, and the results were joined.", language));
        }

        Line(Loc.M("- Deleted pages: a quote that starts with `> **{0}**`, after the marker `<!-- deleted page=N -->`. A page that was marked with an X in the original document. The text and the image reference of the page are not included.", language, Loc.M("[Deleted page]", language)));
        if (hasMergedCells)
        {
            Line(Loc.M("- Merged table cells: `<` is merged with the cell on its left, `^` with the cell above (the notePM style). A Markdown table cannot merge cells, so a cell that is hidden by a merged cell is written as `<` or `^`. The header row cannot be merged: its value is repeated. An empty cell is left empty.", language));
        }

        if (hasJoinedTables)
        {
            Line(Loc.M("- A table that runs over several pages is one table, written where it starts. On the later pages a quote tells that the rows were joined to the table of the first page.", language));
        }

        if (kind == DocumentKind.Xlsx)
        {
            Line(Loc.M("- Sheets (xlsx): `<!-- sheet: name part=2/3 columns=I:P -->`. Written on every page; it shows which sheet and which columns. When a sheet with many columns is split into several images, the part is 2/3 and so on.", language));
        }
    }

    private static string? TryUsageLine(IReadOnlyList<PageRecord> pages, string language)
    {
        var read = pages.Where(p => !p.IsDeleted && !p.IsBlank).ToList();
        if (read.Count == 0)
        {
            return null;
        }

        var withUsage = read.Where(p => p.Usage is { IsAvailable: true }).ToList();
        if (withUsage.Count == 0)
        {
            return Loc.M("> **VLM usage**: **unavailable** (pages: {0}, pages with usage: 0)", language, read.Count);
        }

        string Sum(IEnumerable<int?> values) =>
            values.Any(v => v.HasValue)
                ? values.Sum(v => v ?? 0).ToString(CultureInfo.InvariantCulture)
                : Loc.M("unavailable", language);

        return Loc.M(
            "> **VLM usage**: total prompt={0} completion={1} total={2} (pages: {3}, pages with usage: {4})",
            language,
            Sum(withUsage.Select(p => p.Usage!.PromptTokens)),
            Sum(withUsage.Select(p => p.Usage!.CompletionTokens)),
            Sum(withUsage.Select(p => p.Usage!.TotalTokens)),
            read.Count,
            withUsage.Count);
    }

    private static void AppendPage(
        StringBuilder builder,
        DocumentRecord document,
        PageRecord page,
        IReadOnlyList<int> effectivePages,
        IReadOnlyList<string> blockTexts,
        IReadOnlyDictionary<int, string> vlmTexts,
        bool includeImages,
        bool includeVlm,
        bool vlmOnly,
        string language)
    {
        builder.Append("<!-- page: ").Append(page.Index.ToString(CultureInfo.InvariantCulture)).Append(" -->\n");
        if (page.Sheet is { } sheet)
        {
            builder.Append("<!-- sheet: ").Append(SafeComment(sheet.Name))
                .Append(" part=").Append(sheet.Part.ToString(CultureInfo.InvariantCulture)).Append('/').Append(sheet.PartCount.ToString(CultureInfo.InvariantCulture))
                .Append(" columns=").Append(sheet.ColumnRange).Append(" -->\n");
        }

        if (page.IsDeleted)
        {
            // A mark that does not depend on the language, then the quote in the language of the job.
            builder.Append("<!-- deleted page=").Append(page.Index.ToString(CultureInfo.InvariantCulture)).Append(" -->\n");
            var where = PageLabel.Text(page.SourceLabel, language);
            var notice = page.ImageFile.Length > 0
                ? Loc.M("{0} of the original document was excluded because an X was drawn over the whole page. The image is saved as `{1}`.", language, where, page.ImageFile)
                : Loc.M("{0} of the original document was excluded because an X was drawn over the whole page.", language, where);
            builder.Append("\n> **").Append(Loc.M("[Deleted page]", language)).Append("** ").Append(notice).Append('\n');
            return;
        }

        if (includeImages && page.ImageFile.Length > 0)
        {
            builder.Append("\n![").Append(Loc.M("alt|Page {0}", language, page.Index)).Append("](").Append(page.ImageFile).Append(")\n");
        }

        if (page.IsBlank)
        {
            builder.Append("\n> ").Append(Loc.M("This page is blank.", language)).Append('\n');
        }

        for (var i = 0; i < document.Blocks.Count && !vlmOnly; i++)
        {
            if (effectivePages[i] == page.Index && blockTexts[i].Length > 0)
            {
                builder.Append('\n').Append(MarkdownText.ResolveMarks(blockTexts[i])).Append('\n');
            }
        }

        if (includeVlm)
        {
            AppendVlm(builder, page, vlmTexts);
        }
    }

    /// <summary>
    /// The text of each block as it is written in the Markdown of the document. A table that runs over several pages was cut at the page
    /// breaks (the text sent with each page image is only the rows of the page); here the parts are one table, written where the table starts, and
    /// on the later pages a quote tells where the rows went.
    /// </summary>
    private static string[] JoinTableParts(IReadOnlyList<Block> blocks, IReadOnlyList<int> effectivePages, string language)
    {
        var texts = blocks.Select(b => b.Markdown).ToArray();
        var head = -1;
        var paragraphHead = -1;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Kind == BlockKind.Paragraph)
            {
                // The part of a paragraph after a page break goes back to the paragraph (FR-091).
                if (block.JoinedToPage is null)
                {
                    paragraphHead = i;
                }
                else if (paragraphHead >= 0 && effectivePages[paragraphHead] == block.JoinedToPage)
                {
                    texts[paragraphHead] += ParagraphJoin.Separator(texts[paragraphHead], block.Markdown) + block.Markdown;
                    texts[i] = "> " + Loc.M("The rest of this paragraph is joined to the paragraph on page {0}.", language, block.JoinedToPage.Value);
                }

                continue;
            }

            if (block.Kind is not (BlockKind.Table or BlockKind.SheetTable))
            {
                continue;
            }

            if (block.JoinedToPage is null)
            {
                head = i;
                continue;
            }

            // A part whose first part is not there (its page was a deleted page) stays a table of its own.
            if (head < 0 || effectivePages[head] != block.JoinedToPage)
            {
                continue;
            }

            // The first two lines of a part are its header row (the repeated header, or an empty one): they are not rows of the joined table.
            texts[head] += "\n" + string.Join('\n', block.Markdown.Split('\n').Skip(2));
            texts[i] = "> " + Loc.M("The rows of this table are joined to the table on page {0}.", language, block.JoinedToPage.Value);
        }

        return texts;
    }

    private static void AppendVlm(StringBuilder builder, PageRecord page, IReadOnlyDictionary<int, string> vlmTexts)
    {
        var number = page.Index.ToString(CultureInfo.InvariantCulture);
        switch (page.Step3)
        {
            case PageStep3Status.Skipped when page.IsBlank:
                builder.Append("\n<!-- vlm:skipped page=").Append(number).Append(" reason=\"blank page\" -->\n");
                break;
            case PageStep3Status.Done when page.VlmText is not null:
                builder.Append("\n<!-- vlm:begin page=").Append(number)
                    .Append(page.Bands > 1 ? " bands=" + page.Bands.ToString(CultureInfo.InvariantCulture) : string.Empty)
                    .Append(" -->\n")
                    .Append(MarkdownText.NeutraliseMarkers(vlmTexts.TryGetValue(page.Index, out var joined) ? joined : page.VlmText.Trim()))
                    .Append("\n<!-- vlm:end -->\n");
                builder.Append(UsageComment(page));
                break;
            case PageStep3Status.Failed:
                builder.Append("\n<!-- vlm:failed page=").Append(number).Append(" reason=\"")
                    .Append(SafeComment(page.Error ?? "unknown error").Replace("\"", "'", StringComparison.Ordinal))
                    .Append("\" -->\n");
                break;
        }
    }

    private static string UsageComment(PageRecord page)
    {
        var number = page.Index.ToString(CultureInfo.InvariantCulture);
        if (page.Usage is not { IsAvailable: true } usage)
        {
            return $"<!-- usage page={number} unavailable -->\n";
        }

        string Part(string name, int? value) => value.HasValue ? $" {name}={value.Value.ToString(CultureInfo.InvariantCulture)}" : string.Empty;
        var cost = page.CostUsd is { } c ? " cost=" + c.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty;
        return $"<!-- usage page={number}{Part("prompt", usage.PromptTokens)}{Part("completion", usage.CompletionTokens)}{Part("total", usage.TotalTokens)}{cost} -->\n";
    }

    /// <summary>Text for inside an HTML comment: no <c>--</c> or line breaks, and no secret.</summary>
    private static string SafeComment(string text)
    {
        var cleaned = SecretRegistry.Shared.Redact(text)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("--", "- -", StringComparison.Ordinal);
        return cleaned.Trim();
    }

    /// <summary>Writes the Markdown file of a document atomically (UTF-8 without BOM).</summary>
    public static void Write(DocumentRecord document)
    {
        var name = Path.GetFileNameWithoutExtension(document.SourcePath);
        var path = Path.Combine(document.OutputFolder, MarkdownFileName(name));
        AtomicFile.WriteAllText(path, Render(document));

        var vlmPath = Path.Combine(document.OutputFolder, VlmFileName(name));
        if (document.VlmSeparate && document.Step3 != StepStatus.NotApplicable)
        {
            AtomicFile.WriteAllText(vlmPath, RenderVlm(document));
        }
        else if (File.Exists(vlmPath))
        {
            // The results went back into the Markdown of the document (or Step3 was switched off): the file of an earlier run is of no use.
            try
            {
                File.Delete(vlmPath);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The file of the VLM results of a document (FR-058): the document name plus <c>_vlm.md</c>.</summary>
    public static string VlmFileName(string nameWithoutExtension) =>
        Jobs.OutputNamer.Sanitize(nameWithoutExtension) + "_vlm.md";

    /// <summary>The Markdown file name of a document: the document name without extension plus <c>.md</c>.</summary>
    public static string MarkdownFileName(string nameWithoutExtension) =>
        Jobs.OutputNamer.Sanitize(nameWithoutExtension) + ".md";
}
