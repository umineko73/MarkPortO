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

    public static string Render(DocumentRecord document)
    {
        var includeImages = document.Step2 == StepStatus.Done;
        var includeVlm = document.Step3 != StepStatus.NotApplicable;
        var fileName = Path.GetFileName(document.SourcePath);
        var language = document.MarkdownLanguage;

        var builder = new StringBuilder();
        builder.Append("# ").Append(MarkdownText.Escape(fileName).Replace("\n", " ", StringComparison.Ordinal)).Append("\n\n");
        AppendStructureNote(builder, fileName, document.Kind, includeImages, includeVlm, document.Pages.Any(p => p.Bands > 1),
            document.Blocks.Any(b => b.Markdown.Contains(MarkdownText.MergedCell, StringComparison.Ordinal)), language);

        if (includeVlm && TryUsageLine(document.Pages, language) is { } usageLine)
        {
            builder.Append('\n').Append(usageLine).Append('\n');
            if (document.Pages.Where(p => !p.IsDeleted && p.CostUsd.HasValue).Sum(p => p.CostUsd) is > 0 and var total)
            {
                builder.Append("> ").Append(Loc.M("**VLM cost**: {0} US dollars in total", language, total.ToString("0.####", CultureInfo.InvariantCulture))).Append('\n');
            }
        }

        var effectivePages = DocxPageAssigner.EffectivePages(document.Blocks, document.Pages.Count);
        foreach (var page in document.Pages.OrderBy(p => p.Index))
        {
            builder.Append('\n');
            AppendPage(builder, document, page, effectivePages, includeImages, includeVlm, language);
        }

        return builder.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>The name of the image file of a page: <c>images/page-003.png</c>.</summary>
    public static string ImageFileName(int pageIndex, int pageCount)
    {
        var digits = Math.Max(MinimumImageDigits, pageCount.ToString(CultureInfo.InvariantCulture).Length);
        return "images/page-" + pageIndex.ToString("D" + digits, CultureInfo.InvariantCulture) + ".png";
    }

    private static void AppendStructureNote(StringBuilder builder, string fileName, DocumentKind kind, bool includeImages, bool includeVlm, bool hasBands, bool hasMergedCells, string language)
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
            : Loc.M("- VLM results (Step3): not included in this Markdown.", language));
        if (includeVlm && hasBands)
        {
            Line(Loc.M("- Pages read in bands: `<!-- vlm:begin page=N bands=M -->`. A page with many lines was read in M parts, from the top, and the results were joined.", language));
        }

        Line(Loc.M("- Deleted pages: a quote that starts with `> **{0}**`, after the marker `<!-- deleted page=N -->`. A page that was marked with an X in the original document. The text and the image reference of the page are not included.", language, Loc.M("[Deleted page]", language)));
        if (hasMergedCells)
        {
            Line(Loc.M("- Merged table cells: `{0}`. A Markdown table cannot merge cells, so a cell that is hidden by a merged cell is written as `{0}`. An empty cell is left empty.", language, Loc.M("[merged]", language)));
        }

        if (kind == DocumentKind.Xlsx)
        {
            Line(Loc.M("- Sheets (xlsx): `<!-- sheet: name part=2/3 columns=I:P -->`. Written on every page; it shows which sheet and which columns. When a sheet with many columns is split into several images, the part is 2/3 and so on.", language));
        }
    }

    private static string? TryUsageLine(IReadOnlyList<PageRecord> pages, string language)
    {
        var read = pages.Where(p => !p.IsDeleted).ToList();
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
        bool includeImages,
        bool includeVlm,
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

        for (var i = 0; i < document.Blocks.Count; i++)
        {
            if (effectivePages[i] == page.Index && document.Blocks[i].Markdown.Length > 0)
            {
                builder.Append('\n').Append(MarkdownText.ResolveMarks(document.Blocks[i].Markdown, language)).Append('\n');
            }
        }

        if (includeVlm)
        {
            AppendVlm(builder, page);
        }
    }

    private static void AppendVlm(StringBuilder builder, PageRecord page)
    {
        var number = page.Index.ToString(CultureInfo.InvariantCulture);
        switch (page.Step3)
        {
            case PageStep3Status.Done when page.VlmText is not null:
                builder.Append("\n<!-- vlm:begin page=").Append(number)
                    .Append(page.Bands > 1 ? " bands=" + page.Bands.ToString(CultureInfo.InvariantCulture) : string.Empty)
                    .Append(" -->\n")
                    .Append(MarkdownText.NeutraliseMarkers(page.VlmText.Trim()))
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
    }

    /// <summary>The Markdown file name of a document: the document name without extension plus <c>.md</c>.</summary>
    public static string MarkdownFileName(string nameWithoutExtension) =>
        Jobs.OutputNamer.Sanitize(nameWithoutExtension) + ".md";
}
