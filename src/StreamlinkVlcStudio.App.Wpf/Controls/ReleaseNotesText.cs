using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using StreamlinkVlcStudio.App.Wpf.Services;

namespace StreamlinkVlcStudio.App.Wpf.Controls;

public enum ReleaseNotesPresentation
{
    Document,
    Header,
    Changes
}

/// <summary>Selectable, themed release notes with the headings, lists, code and links used by docs/releases.</summary>
public sealed class ReleaseNotesText : RichTextBox
{
    private static readonly Regex InlinePattern = new(
        @"`(?<code>[^`]+)`|\*\*(?<bold>.+?)\*\*|\[(?<label>[^\]]+)\]\((?<target>[^\s)]+)\)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(ReleaseNotesText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure,
            (source, _) => ((ReleaseNotesText)source).RenderNotes()));

    public static readonly DependencyProperty PresentationProperty = DependencyProperty.Register(
        nameof(Presentation), typeof(ReleaseNotesPresentation), typeof(ReleaseNotesText),
        new FrameworkPropertyMetadata(ReleaseNotesPresentation.Document, FrameworkPropertyMetadataOptions.AffectsMeasure,
            (source, _) => ((ReleaseNotesText)source).RenderNotes()));

    public ReleaseNotesText()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        IsReadOnlyCaretVisible = false;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(SelectionBrushProperty, "StudioAccentPressedBrush");
        RenderNotes();
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public ReleaseNotesPresentation Presentation
    {
        get => (ReleaseNotesPresentation)GetValue(PresentationProperty);
        set => SetValue(PresentationProperty, value);
    }

    private void RenderNotes()
    {
        var isHeader = Presentation == ReleaseNotesPresentation.Header;
        var isChanges = Presentation == ReleaseNotesPresentation.Changes;
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 14,
            LineHeight = 22
        };
        document.SetResourceReference(TextElement.ForegroundProperty, "StudioTextBrush");
        var paragraphText = new StringBuilder();
        List? list = null;
        var featureList = false;
        List<string>? codeLines = null;
        var codeFence = new MarkdownCodeFence();

        void FlushParagraph()
        {
            if (paragraphText.Length == 0) return;
            var paragraph = CreateParagraph(paragraphText.ToString());
            if (isHeader)
            {
                paragraph.Margin = new Thickness(0);
                paragraph.SetResourceReference(TextElement.ForegroundProperty, "StudioTextSecondaryBrush");
            }
            document.Blocks.Add(paragraph);
            paragraphText.Clear();
        }

        foreach (var rawLine in (Markdown ?? "").ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (codeFence.TryProcessDelimiter(rawLine))
            {
                FlushParagraph();
                list = null;
                if (codeLines is null) codeLines = [];
                else
                {
                    document.Blocks.Add(CreateCodeParagraph(string.Join('\n', codeLines)));
                    codeLines = null;
                }
                continue;
            }

            if (codeLines is not null)
            {
                codeLines.Add(codeFence.RemoveIndentation(rawLine));
                continue;
            }

            if (line.Length == 0)
            {
                FlushParagraph();
                list = null;
                continue;
            }

            var headingLevel = line.TakeWhile(character => character == '#').Count();
            if (headingLevel is > 0 and <= 6 && line.Length > headingLevel && line[headingLevel] == ' ')
            {
                FlushParagraph();
                list = null;
                var heading = CreateParagraph(line[(headingLevel + 1)..]);
                heading.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
                heading.FontSize = headingLevel == 1 ? isHeader ? 30 : 26 : headingLevel == 2 ? 18 : 15;
                heading.FontWeight = FontWeights.SemiBold;
                heading.LineHeight = headingLevel == 1 ? isHeader ? 38 : 32 : 26;
                heading.Margin = new Thickness(0, document.Blocks.Count == 0 ? 0 : 24, 0, 10);
                heading.KeepWithNext = true;
                if (headingLevel == 2 && !isChanges)
                {
                    heading.SetResourceReference(Block.BorderBrushProperty, "StudioBorderBrush");
                    heading.BorderThickness = new Thickness(0, 0, 0, 1);
                    heading.Padding = new Thickness(0, 0, 0, 8);
                }
                document.Blocks.Add(heading);
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) ||
                line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("+ ", StringComparison.Ordinal))
            {
                FlushParagraph();
                var text = line[2..];
                var leadEnd = isChanges && text.StartsWith("**", StringComparison.Ordinal)
                    ? text.IndexOf("**", 2, StringComparison.Ordinal) : -1;
                var isFeature = leadEnd > 2;
                if (list is null || featureList != isFeature)
                {
                    featureList = isFeature;
                    list = new List
                    {
                        MarkerStyle = isFeature ? TextMarkerStyle.None : TextMarkerStyle.Disc,
                        MarkerOffset = 10,
                        Margin = new Thickness(isFeature ? 0 : 18, 0, 0, 6),
                        Padding = new Thickness(0)
                    };
                    document.Blocks.Add(list);
                }
                var item = isFeature ? CreateFeatureParagraph(text, leadEnd) : CreateParagraph(text);
                item.Margin = new Thickness(0, 0, 0, isFeature ? 0 : 10);
                var listItem = new ListItem(item);
                if (isFeature)
                {
                    listItem.Padding = new Thickness(0, 0, 0, 12);
                }
                list.ListItems.Add(listItem);
                continue;
            }

            // A wrapped list item is a continuation, not a separate paragraph.
            if (list is not null && rawLine.Length > 0 && char.IsWhiteSpace(rawLine[0]))
            {
                var item = (Paragraph)list.ListItems.LastListItem.Blocks.LastBlock;
                if (featureList && !item.Inlines.OfType<LineBreak>().Any()) item.Inlines.Add(new LineBreak());
                else item.Inlines.Add(new Run(" "));
                AddInlines(item.Inlines, line);
                continue;
            }

            list = null;
            if (paragraphText.Length > 0) paragraphText.Append(' ');
            paragraphText.Append(line);
        }

        FlushParagraph();
        if (codeLines is not null) document.Blocks.Add(CreateCodeParagraph(string.Join('\n', codeLines)));
        Document = document;
    }

    private static Paragraph CreateParagraph(string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 14) };
        AddInlines(paragraph.Inlines, text);
        return paragraph;
    }

    private static Paragraph CreateCodeParagraph(string text)
    {
        var paragraph = new Paragraph(new Run(text))
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            LineHeight = 21,
            Margin = new Thickness(0, 4, 0, 16),
            Padding = new Thickness(14),
            BorderThickness = new Thickness(1)
        };
        paragraph.SetResourceReference(TextElement.BackgroundProperty, "StudioSurface0Brush");
        paragraph.SetResourceReference(Block.BorderBrushProperty, "StudioBorderBrush");
        return paragraph;
    }

    private static Paragraph CreateFeatureParagraph(string text, int leadEnd)
    {
        var paragraph = new Paragraph();
        paragraph.SetResourceReference(TextElement.ForegroundProperty, "StudioTextSecondaryBrush");
        var lead = new Bold { FontWeight = FontWeights.SemiBold, FontSize = 14 };
        lead.SetResourceReference(TextElement.ForegroundProperty, "StudioTextBrush");
        AddInlines(lead.Inlines, text[2..leadEnd]);
        paragraph.Inlines.Add(lead);
        var description = text[(leadEnd + 2)..].TrimStart();
        if (description.Length > 0)
        {
            paragraph.Inlines.Add(new LineBreak());
            AddInlines(paragraph.Inlines, description);
        }
        return paragraph;
    }

    private static void AddInlines(InlineCollection inlines, string text)
    {
        var offset = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > offset) inlines.Add(new Run(text[offset..match.Index]));
            if (match.Groups["code"].Success)
            {
                var code = new Run(match.Groups["code"].Value)
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 13
                };
                code.SetResourceReference(TextElement.BackgroundProperty, "StudioSurface2Brush");
                inlines.Add(code);
            }
            else if (match.Groups["bold"].Success)
            {
                var bold = new Bold { FontWeight = FontWeights.SemiBold };
                bold.SetResourceReference(TextElement.ForegroundProperty, "StudioTextBrush");
                AddInlines(bold.Inlines, match.Groups["bold"].Value);
                inlines.Add(bold);
            }
            else
            {
                var label = match.Groups["label"].Value;
                if (ResolveLink(match.Groups["target"].Value) is { } uri)
                {
                    var link = new Hyperlink(new Run(label)) { NavigateUri = uri };
                    link.SetResourceReference(TextElement.ForegroundProperty, "StudioAccentTextBrush");
                    inlines.Add(link);
                }
                else inlines.Add(new Run(match.Value));
            }
            offset = match.Index + match.Length;
        }
        if (offset < text.Length) inlines.Add(new Run(text[offset..]));
    }

    private static Uri? ResolveLink(string target)
    {
        if (target.StartsWith('v') && target.EndsWith(".md", StringComparison.Ordinal) &&
            Version.TryParse(target[1..^3], out var version) && version.Build >= 0 && version.Revision < 0)
            return new Uri($"https://github.com/CorontoSiete/streamlink-vlc-studio/releases/tag/v{version.ToString(3)}");
        return Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
               uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;
    }
}
