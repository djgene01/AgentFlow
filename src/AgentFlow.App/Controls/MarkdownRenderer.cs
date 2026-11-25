using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;
using WpfTableRow = System.Windows.Documents.TableRow;
using WpfTableCell = System.Windows.Documents.TableCell;

namespace AgentFlow.App.Controls;

/// <summary>Renders Markdown to a FlowDocument: headings, paragraphs, lists, quotes, code, tables, links.</summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .Build();

    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");

    public static FlowDocument Render(string markdown, FlowDocument? reuse = null)
    {
        var doc = reuse ?? new FlowDocument();
        doc.Blocks.Clear();
        doc.PagePadding = new Thickness(0);
        doc.TextAlignment = TextAlignment.Left;

        var parsed = Markdown.Parse(markdown ?? "", Pipeline);
        foreach (var block in parsed)
        {
            if (RenderBlock(block) is { } b) doc.Blocks.Add(b);
        }
        return doc;
    }

    private static WpfBlock? RenderBlock(MdBlock block) => block switch
    {
        HeadingBlock h => Heading(h),
        ParagraphBlock p => ParagraphOf(p),
        ListBlock l => ListOf(l),
        QuoteBlock q => Quote(q),
        FencedCodeBlock f => Code(f),
        CodeBlock c => Code(c),
        ThematicBreakBlock => new BlockUIContainer(new System.Windows.Controls.Separator { Margin = new Thickness(0, 6, 0, 6) }),
        Markdig.Extensions.Tables.Table t => TableOf(t),
        HtmlBlock html => new Paragraph(new Run(html.Lines.ToString())) { FontFamily = Mono },
        _ => null,
    };

    private static Paragraph Heading(HeadingBlock h)
    {
        var p = new Paragraph { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
        p.FontSize = h.Level switch { 1 => 20, 2 => 17, 3 => 15, _ => 14 };
        AddInlines(p.Inlines, h.Inline);
        return p;
    }

    private static Paragraph ParagraphOf(ParagraphBlock pb)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        AddInlines(p.Inlines, pb.Inline);
        return p;
    }

    private static List ListOf(ListBlock l)
    {
        var list = new List
        {
            MarkerStyle = l.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(20, 0, 0, 0),
        };
        if (l.IsOrdered && int.TryParse(l.OrderedStart, out var start)) list.StartIndex = start;

        foreach (var item in l.OfType<ListItemBlock>())
        {
            var li = new ListItem();
            foreach (var child in item)
            {
                if (RenderBlock(child) is { } b)
                {
                    if (b is Paragraph para) para.Margin = new Thickness(0, 0, 0, 2);
                    li.Blocks.Add(b);
                }
            }
            list.ListItems.Add(li);
        }
        return list;
    }

    private static Section Quote(QuoteBlock q)
    {
        var s = new Section
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 8),
        };
        s.SetResourceReference(Section.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        s.SetResourceReference(Section.ForegroundProperty, "TextFillColorSecondaryBrush");
        foreach (var child in q)
            if (RenderBlock(child) is { } b) s.Blocks.Add(b);
        return s;
    }

    private static Paragraph Code(LeafBlock c)
    {
        var text = c.Lines.ToString().TrimEnd('\r', '\n');
        var p = new Paragraph(new Run(text))
        {
            FontFamily = Mono,
            FontSize = 12.5,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
        };
        p.SetResourceReference(Paragraph.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        return p;
    }

    private static WpfTable TableOf(Markdig.Extensions.Tables.Table t)
    {
        var table = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1) };
        table.SetResourceReference(WpfTable.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        foreach (var row in t.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var tr = new WpfTableRow();
            if (row.IsHeader) tr.FontWeight = FontWeights.SemiBold;
            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var tc = new WpfTableCell { Padding = new Thickness(6, 3, 6, 3), BorderThickness = new Thickness(0, 0, 1, 1) };
                tc.SetResourceReference(WpfTableCell.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
                foreach (var child in cell)
                    if (RenderBlock(child) is { } b)
                    {
                        if (b is Paragraph para) para.Margin = new Thickness(0);
                        tc.Blocks.Add(b);
                    }
                tr.Cells.Add(tc);
            }
            group.Rows.Add(tr);
        }
        return table;
    }

    private static void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null) return;
        foreach (var inline in container)
        {
            if (RenderInline(inline) is { } i) target.Add(i);
        }
    }

    private static WpfInline? RenderInline(MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline lit:
                return new Run(lit.Content.ToString());

            case CodeInline code:
            {
                var r = new Run(code.Content) { FontFamily = Mono, FontSize = 12.5 };
                r.SetResourceReference(Run.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                return r;
            }

            case LineBreakInline br:
                return br.IsHard ? new LineBreak() : new Run(" ");

            case TaskList task:
                return new Run(task.Checked ? "☑ " : "☐ ");

            case EmphasisInline em:
            {
                Span span = em.DelimiterChar switch
                {
                    '~' => new Span { TextDecorations = TextDecorations.Strikethrough },
                    _ when em.DelimiterCount >= 2 => new Bold(),
                    _ => new Italic(),
                };
                AddInlines(span.Inlines, em);
                return span;
            }

            case LinkInline link when link.IsImage:
                return new Run($"[image: {link.Url}]");

            case LinkInline link:
            {
                var h = new Hyperlink { ToolTip = link.Url };
                AddInlines(h.Inlines, link);
                if (h.Inlines.Count == 0) h.Inlines.Add(new Run(link.Url));
                AttachNavigation(h, link.Url);
                return h;
            }

            case AutolinkInline auto:
            {
                var h = new Hyperlink(new Run(auto.Url));
                AttachNavigation(h, auto.IsEmail ? null : auto.Url);
                return h;
            }

            case HtmlInline html:
                return new Run(html.Tag);

            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());

            case ContainerInline c:
            {
                var span = new Span();
                AddInlines(span.Inlines, c);
                return span;
            }

            default:
                return null;
        }
    }

    /// <summary>Only http(s) links open, in the default browser.</summary>
    private static void AttachNavigation(Hyperlink h, string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;
        h.Click += (_, _) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        h.Cursor = System.Windows.Input.Cursors.Hand;
    }
}
