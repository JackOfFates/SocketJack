using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace heirowLLM;

/// <summary>
/// Selectable, copyable Markdown presentation used by the native Chicken Chaser window.
/// It intentionally renders the same common Markdown constructs as Web Chat without
/// allowing model output to inject XAML or HTML.
/// </summary>
internal sealed class ChickenChaserMarkdownMessage : Grid
{
    private readonly RichTextBox _viewer;
    private readonly Button _copyButton;
    private string _markdown = "";

    public ChickenChaserMarkdownMessage(bool user, bool application)
    {
        Margin = new Thickness(user ? 42 : 0, 4, user ? 0 : 42, 4);

        Brush foreground = application
            ? new SolidColorBrush(Color.FromArgb(204, 187, 247, 208))
            : new SolidColorBrush(Color.FromArgb(204, 255, 255, 255));
        Brush background = user
            ? new SolidColorBrush(Color.FromArgb(84, 56, 36, 93))
            : application
                ? new SolidColorBrush(Color.FromArgb(84, 21, 56, 45))
                : new SolidColorBrush(Color.FromArgb(84, 24, 34, 49));

        _viewer = new RichTextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            IsDocumentEnabled = true,
            AcceptsReturn = true,
            Background = background,
            Foreground = foreground,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(9, 7, 54, 7),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            SelectionBrush = new SolidColorBrush(Color.FromArgb(190, 37, 99, 235)),
            SelectionOpacity = .8,
            Cursor = Cursors.IBeam,
            ToolTip = "Select text and press Ctrl+C, or use Copy"
        };
        _viewer.Document = ChickenChaserMarkdownRenderer.CreateDocument("", foreground);
        Children.Add(_viewer);

        _copyButton = new Button
        {
            Content = "Copy",
            Width = 44,
            Height = 24,
            Margin = new Thickness(0, 6, 6, 0),
            Padding = new Thickness(3, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(174, 15, 23, 42)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 148, 163, 184)),
            BorderThickness = new Thickness(1),
            Foreground = foreground,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            ToolTip = "Copy message text"
        };
        _copyButton.Click += (_, _) => CopyMessage();
        Children.Add(_copyButton);

        var contextMenu = new ContextMenu();
        var copySelection = new MenuItem { Header = "Copy selection", Command = ApplicationCommands.Copy, CommandTarget = _viewer };
        var copyMessage = new MenuItem { Header = "Copy message" };
        copyMessage.Click += (_, _) => CopyMessage();
        contextMenu.Items.Add(copySelection);
        contextMenu.Items.Add(copyMessage);
        contextMenu.Opened += (_, _) => copySelection.IsEnabled = !_viewer.Selection.IsEmpty;
        _viewer.ContextMenu = contextMenu;
    }

    public string Markdown => _markdown;

    public void AppendMarkdown(string value) => SetMarkdown(_markdown + (value ?? ""));

    public void SetMarkdown(string value)
    {
        _markdown = value ?? "";
        _viewer.Document = ChickenChaserMarkdownRenderer.CreateDocument(_markdown, _viewer.Foreground);
        _copyButton.Visibility = string.IsNullOrWhiteSpace(_markdown) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CopyMessage()
    {
        if (string.IsNullOrWhiteSpace(_markdown)) return;
        try
        {
            Clipboard.SetText(_markdown);
            _copyButton.Content = "Copied";
            _copyButton.Width = 52;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _copyButton.Content = "Copy";
                _copyButton.Width = 44;
            };
            timer.Start();
        }
        catch
        {
            _viewer.Focus();
            _viewer.SelectAll();
        }
    }
}

internal static class ChickenChaserMarkdownRenderer
{
    private static readonly Regex InlineMarkdown = new(
        @"(`+)(.+?)\1|\*\*(.+?)\*\*|__(.+?)__|~~(.+?)~~|\[([^\]]+)\]\(([^)\s]+)\)|(?<!\*)\*([^*\r\n]+)\*(?!\*)|(?<!_)_([^_\r\n]+)_(?!_)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static FlowDocument CreateDocument(string markdown, Brush foreground)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnGap = 0,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            Foreground = foreground,
            LineHeight = double.NaN,
            TextAlignment = TextAlignment.Left
        };

        RenderBlocks(document.Blocks, Normalize(markdown));
        if (document.Blocks.Count == 0)
            document.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
        return document;
    }

    private static void RenderBlocks(BlockCollection target, string markdown)
    {
        string[] lines = markdown.Split('\n');
        int index = 0;
        while (index < lines.Length)
        {
            string line = lines[index];
            if (string.IsNullOrWhiteSpace(line)) { index++; continue; }

            Match fence = Regex.Match(line, @"^\s{0,3}(`{3,}|~{3,})\s*([^\s`]*)?.*$");
            if (fence.Success)
            {
                string marker = fence.Groups[1].Value;
                string language = fence.Groups[2].Value;
                index++;
                var code = new List<string>();
                while (index < lines.Length && !Regex.IsMatch(lines[index], @"^\s{0,3}" + Regex.Escape(marker[0].ToString()) + "{" + marker.Length + @",}\s*$"))
                    code.Add(lines[index++]);
                if (index < lines.Length) index++;
                target.Add(CreateCodeBlock(code, language));
                continue;
            }

            Match heading = Regex.Match(line, @"^(#{1,6})\s+(.+?)\s*#*\s*$");
            if (heading.Success)
            {
                int level = heading.Groups[1].Value.Length;
                var paragraph = CreateParagraph(heading.Groups[2].Value, new Thickness(0, level == 1 ? 5 : 3, 0, 5));
                paragraph.FontSize = Math.Max(13, 22 - (level * 1.7));
                paragraph.FontWeight = FontWeights.Bold;
                target.Add(paragraph);
                index++;
                continue;
            }

            if (Regex.IsMatch(line, @"^\s{0,3}([-*_])(?:\s*\1){2,}\s*$"))
            {
                target.Add(new BlockUIContainer(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 7, 0, 7),
                    Background = new SolidColorBrush(Color.FromArgb(120, 148, 163, 184))
                }) { Margin = new Thickness(0) });
                index++;
                continue;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                var quoted = new List<string>();
                while (index < lines.Length && (lines[index].TrimStart().StartsWith('>') || string.IsNullOrWhiteSpace(lines[index])))
                {
                    quoted.Add(Regex.Replace(lines[index], @"^\s{0,3}>\s?", ""));
                    index++;
                }
                var section = new Section
                {
                    Margin = new Thickness(2, 3, 0, 5),
                    Padding = new Thickness(9, 2, 2, 2),
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(180, 125, 211, 252)),
                    Foreground = new SolidColorBrush(Color.FromArgb(220, 203, 213, 225))
                };
                RenderBlocks(section.Blocks, string.Join('\n', quoted));
                target.Add(section);
                continue;
            }

            if (IsTableStart(lines, index))
            {
                target.Add(CreateTable(lines, ref index));
                continue;
            }

            Match listItem = Regex.Match(line, @"^\s{0,3}([-*+]|\d+[.)])\s+(.+)$");
            if (listItem.Success)
            {
                bool ordered = char.IsDigit(listItem.Groups[1].Value[0]);
                var list = new System.Windows.Documents.List
                {
                    MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(18, 2, 0, 5),
                    Padding = new Thickness(0)
                };
                while (index < lines.Length)
                {
                    Match item = Regex.Match(lines[index], ordered
                        ? @"^\s{0,3}\d+[.)]\s+(.+)$"
                        : @"^\s{0,3}[-*+]\s+(.+)$");
                    if (!item.Success) break;
                    string itemText = Regex.Replace(item.Groups[1].Value, @"^\[ \]\s+", "☐ ", RegexOptions.IgnoreCase);
                    itemText = Regex.Replace(itemText, @"^\[[xX]\]\s+", "☑ ");
                    list.ListItems.Add(new ListItem(CreateParagraph(itemText, new Thickness(0, 1, 0, 1))));
                    index++;
                }
                target.Add(list);
                continue;
            }

            var paragraphLines = new List<string>();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) && !IsBlockStart(lines, index))
                paragraphLines.Add(lines[index++].Trim());
            if (paragraphLines.Count == 0)
            {
                paragraphLines.Add(lines[index++].Trim());
            }
            target.Add(CreateParagraph(string.Join('\n', paragraphLines), new Thickness(0, 1, 0, 5)));
        }
    }

    private static bool IsBlockStart(string[] lines, int index)
    {
        string line = lines[index];
        return Regex.IsMatch(line, @"^\s{0,3}(`{3,}|~{3,})") ||
            Regex.IsMatch(line, @"^#{1,6}\s+") ||
            Regex.IsMatch(line, @"^\s{0,3}([-*_])(?:\s*\1){2,}\s*$") ||
            line.TrimStart().StartsWith('>') ||
            Regex.IsMatch(line, @"^\s{0,3}([-*+]|\d+[.)])\s+") ||
            IsTableStart(lines, index);
    }

    private static Paragraph CreateParagraph(string text, Thickness margin)
    {
        var paragraph = new Paragraph { Margin = margin, LineHeight = double.NaN };
        AppendInline(paragraph.Inlines, text);
        return paragraph;
    }

    private static Paragraph CreateCodeBlock(IEnumerable<string> lines, string language)
    {
        string code = string.Join('\n', lines);
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 3, 0, 6),
            Padding = new Thickness(8),
            Background = new SolidColorBrush(Color.FromArgb(190, 2, 6, 23)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 71, 85, 105)),
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11.5
        };
        if (!string.IsNullOrWhiteSpace(language))
            paragraph.Inlines.Add(new Run(language + "\n") { Foreground = new SolidColorBrush(Color.FromRgb(125, 211, 252)), FontSize = 10, FontWeight = FontWeights.Bold });
        paragraph.Inlines.Add(new Run(code));
        return paragraph;
    }

    private static bool IsTableStart(string[] lines, int index)
    {
        if (index + 1 >= lines.Length || !lines[index].Contains('|')) return false;
        return Regex.IsMatch(lines[index + 1], @"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$");
    }

    private static Table CreateTable(string[] lines, ref int index)
    {
        string[] headers = SplitTableRow(lines[index]);
        index += 2;
        var rows = new List<string[]>();
        while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) && lines[index].Contains('|'))
            rows.Add(SplitTableRow(lines[index++]));

        int width = Math.Max(headers.Length, rows.Count == 0 ? 0 : rows.Max(row => row.Length));
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 3, 0, 7),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 71, 85, 105)),
            BorderThickness = new Thickness(1)
        };
        for (int i = 0; i < width; i++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        group.Rows.Add(CreateTableRow(headers, width, true));
        foreach (string[] row in rows) group.Rows.Add(CreateTableRow(row, width, false));
        table.RowGroups.Add(group);
        return table;
    }

    private static TableRow CreateTableRow(string[] cells, int width, bool header)
    {
        var row = new TableRow();
        for (int i = 0; i < width; i++)
        {
            var paragraph = CreateParagraph(i < cells.Length ? cells[i] : "", new Thickness(0));
            if (header) paragraph.FontWeight = FontWeights.Bold;
            row.Cells.Add(new TableCell(paragraph)
            {
                Padding = new Thickness(5, 3, 5, 3),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 71, 85, 105)),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Background = header ? new SolidColorBrush(Color.FromArgb(110, 30, 41, 59)) : Brushes.Transparent
            });
        }
        return row;
    }

    private static string[] SplitTableRow(string line)
    {
        string value = line.Trim();
        if (value.StartsWith('|')) value = value[1..];
        if (value.EndsWith('|')) value = value[..^1];
        return Regex.Split(value, @"(?<!\\)\|").Select(cell => cell.Trim().Replace(@"\|", "|")).ToArray();
    }

    private static void AppendInline(InlineCollection target, string text)
    {
        string[] lines = text.Split('\n');
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (lineIndex > 0) target.Add(new LineBreak());
            AppendInlineLine(target, lines[lineIndex]);
        }
    }

    private static void AppendInlineLine(InlineCollection target, string text)
    {
        int cursor = 0;
        foreach (Match match in InlineMarkdown.Matches(text))
        {
            if (match.Index > cursor) target.Add(new Run(text[cursor..match.Index]));
            if (match.Groups[2].Success)
            {
                target.Add(new Run(match.Groups[2].Value)
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 11.5,
                    Background = new SolidColorBrush(Color.FromArgb(170, 2, 6, 23))
                });
            }
            else if (match.Groups[3].Success || match.Groups[4].Success)
            {
                target.Add(new Bold(new Run(match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value)));
            }
            else if (match.Groups[5].Success)
            {
                target.Add(new Run(match.Groups[5].Value) { TextDecorations = TextDecorations.Strikethrough });
            }
            else if (match.Groups[6].Success)
            {
                string label = match.Groups[6].Value;
                string address = match.Groups[7].Value;
                if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto))
                {
                    var link = new Hyperlink(new Run(label)) { NavigateUri = uri, Foreground = new SolidColorBrush(Color.FromRgb(125, 211, 252)), ToolTip = address };
                    link.RequestNavigate += (_, e) =>
                    {
                        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                        e.Handled = true;
                    };
                    target.Add(link);
                }
                else target.Add(new Run(label + " (" + address + ")"));
            }
            else
            {
                string value = match.Groups[8].Success ? match.Groups[8].Value : match.Groups[9].Value;
                target.Add(new Italic(new Run(value)));
            }
            cursor = match.Index + match.Length;
        }
        if (cursor < text.Length) target.Add(new Run(text[cursor..]));
    }

    private static string Normalize(string value) => (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
