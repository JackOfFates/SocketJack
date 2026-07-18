using System.Text.RegularExpressions;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Storage;

namespace JackLLM.Mobile.Controls;

public sealed class MarkdownMessageView : ContentView
{
    public static readonly BindableProperty MarkdownProperty = BindableProperty.Create(
        nameof(Markdown), typeof(string), typeof(MarkdownMessageView), "", propertyChanged: OnMarkdownChanged);

    private readonly VerticalStackLayout _blocks = new() { Spacing = 7 };
    private int _renderScheduled;
    private string _rendered = "";

    public MarkdownMessageView() => Content = _blocks;

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((MarkdownMessageView)bindable).ScheduleRender();

    private void ScheduleRender()
    {
        if (Interlocked.Exchange(ref _renderScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(60);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Render(Markdown ?? "");
                Interlocked.Exchange(ref _renderScheduled, 0);
                if (!string.Equals(_rendered, Markdown, StringComparison.Ordinal)) ScheduleRender();
            });
        });
    }

    private void Render(string markdown)
    {
        if (string.Equals(_rendered, markdown, StringComparison.Ordinal)) return;
        _rendered = markdown;
        _blocks.Children.Clear();
        string displayText = ModelOutputSanitizer.Sanitize(markdown);
        if (string.IsNullOrWhiteSpace(displayText)) return;

        string[] lines = displayText.Split('\n');
        var paragraph = new List<string>();
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(paragraph);
                string language = line.Trim()[3..].Trim();
                var code = new List<string>();
                while (++index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[index]);
                AddCodeBlock(language, string.Join("\n", code));
                continue;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph(paragraph);
                continue;
            }
            if (IsMarkdownTableStart(lines, index))
            {
                FlushParagraph(paragraph);
                string[] headers = SplitMarkdownTableRow(line);
                var rows = new List<string[]>();
                index += 2;
                while (index < lines.Length && IsPipeTableRow(lines[index]))
                {
                    rows.Add(SplitMarkdownTableRow(lines[index]));
                    index++;
                }
                AddTable(headers, rows);
                index--;
                continue;
            }
            if (TryParseLooseMarkdownTable(lines, index, out string[] looseHeaders, out List<string[]> looseRows, out int looseNextIndex))
            {
                FlushParagraph(paragraph);
                AddTable(looseHeaders, looseRows);
                index = looseNextIndex - 1;
                continue;
            }
            Match heading = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (heading.Success)
            {
                FlushParagraph(paragraph);
                int level = heading.Groups[1].Value.Length;
                AddText(heading.Groups[2].Value, Math.Max(16, 25 - level * 2), FontAttributes.Bold, Color.FromArgb("#F8FAFC"));
                continue;
            }
            Match bullet = Regex.Match(line, @"^\s*[-*+]\s+(.+)$");
            Match numbered = Regex.Match(line, @"^\s*(\d+)\.\s+(.+)$");
            if (bullet.Success || numbered.Success)
            {
                FlushParagraph(paragraph);
                string prefix = bullet.Success ? "•  " : numbered.Groups[1].Value + ".  ";
                AddText(prefix + (bullet.Success ? bullet.Groups[1].Value : numbered.Groups[2].Value), 15, FontAttributes.None, Colors.White, new Thickness(8, 0, 0, 0));
                continue;
            }
            if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                FlushParagraph(paragraph);
                var quote = CreateInlineLabel(line.TrimStart()[1..].TrimStart(), 14, FontAttributes.Italic, Color.FromArgb("#CBD5E1"));
                _blocks.Children.Add(new Border { Padding = new Thickness(10, 6), StrokeThickness = 0, BackgroundColor = Color.FromArgb("#17233A"), StrokeShape = new RoundRectangle { CornerRadius = 8 }, Content = quote });
                continue;
            }
            if (Regex.IsMatch(line.Trim(), @"^([-*_])\1{2,}$"))
            {
                FlushParagraph(paragraph);
                _blocks.Children.Add(new BoxView { HeightRequest = 1, Color = Color.FromArgb("#475569"), Margin = new Thickness(0, 4) });
                continue;
            }
            paragraph.Add(line);
        }
        FlushParagraph(paragraph);
        RequestFreshLayout();
    }

    private void RequestFreshLayout()
    {
        _blocks.InvalidateMeasure();
        InvalidateMeasure();

        // CollectionView virtualizes its children and can retain the height it
        // measured while this renderer was still empty. Re-measure the complete
        // bubble after streamed Markdown creates or changes its block children.
        Element? ancestor = Parent;
        while (ancestor is not null)
        {
            if (ancestor is Border border)
            {
                border.InvalidateMeasure();
                break;
            }
            ancestor = ancestor.Parent;
        }
    }

    private void FlushParagraph(List<string> lines)
    {
        if (lines.Count == 0) return;
        AddText(string.Join("\n", lines), 15, FontAttributes.None, Colors.White);
        lines.Clear();
    }

    private void AddText(string markdown, double size, FontAttributes attributes, Color color, Thickness? margin = null)
    {
        Label label = CreateInlineLabel(markdown, size, attributes, color);
        if (margin.HasValue) label.Margin = margin.Value;
        _blocks.Children.Add(label);
    }

    private static Label CreateInlineLabel(string markdown, double size, FontAttributes attributes, Color color)
    {
        var label = new Label { FontSize = size, TextColor = color, FontAttributes = attributes, LineBreakMode = LineBreakMode.WordWrap };
        var formatted = new FormattedString();
        Regex tokenPattern = new(@"(\*\*.+?\*\*|`[^`]+`|\*[^*\n]+\*|\[[^\]]+\]\([^)]+\))", RegexOptions.Singleline);
        int cursor = 0;
        foreach (Match match in tokenPattern.Matches(markdown))
        {
            if (match.Index > cursor) formatted.Spans.Add(new Span { Text = markdown[cursor..match.Index] });
            string token = match.Value;
            if (token.StartsWith("**", StringComparison.Ordinal))
                formatted.Spans.Add(new Span { Text = token[2..^2], FontAttributes = FontAttributes.Bold });
            else if (token.StartsWith('`'))
                formatted.Spans.Add(new Span { Text = token[1..^1], FontFamily = "monospace", BackgroundColor = Color.FromArgb("#334155"), TextColor = Color.FromArgb("#E2E8F0") });
            else if (token.StartsWith('*'))
                formatted.Spans.Add(new Span { Text = token[1..^1], FontAttributes = FontAttributes.Italic });
            else
            {
                Match link = Regex.Match(token, @"^\[([^\]]+)\]\(([^)]+)\)$");
                var span = new Span { Text = link.Groups[1].Value, TextColor = Color.FromArgb("#60A5FA"), TextDecorations = TextDecorations.Underline };
                string url = link.Groups[2].Value;
                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) => { if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) await Browser.Default.OpenAsync(uri); };
                span.GestureRecognizers.Add(tap);
                formatted.Spans.Add(span);
            }
            cursor = match.Index + match.Length;
        }
        if (cursor < markdown.Length) formatted.Spans.Add(new Span { Text = markdown[cursor..] });
        label.FormattedText = formatted;
        return label;
    }

    private static bool IsMarkdownTableStart(string[] lines, int index) =>
        index + 1 < lines.Length &&
        lines[index].Contains('|') &&
        Regex.IsMatch(lines[index + 1], @"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$");

    private static bool IsPipeTableRow(string line)
    {
        string value = line.Trim();
        return value.Length > 2 && value.Contains('|');
    }

    private static string[] SplitMarkdownTableRow(string line)
    {
        string value = line.Trim();
        if (value.StartsWith('|')) value = value[1..];
        if (value.EndsWith('|')) value = value[..^1];
        return value.Split('|').Select(cell => cell.Trim()).ToArray();
    }

    private static bool TryParseLooseMarkdownTable(string[] lines, int index, out string[] headers, out List<string[]> rows, out int nextIndex)
    {
        headers = [];
        rows = [];
        nextIndex = index;
        string line = lines[index];
        if (!IsPipeTableRow(line)) return false;
        string[] cells = SplitMarkdownTableRow(line);
        if (cells.Length < 2 || cells.Any(cell => string.IsNullOrWhiteSpace(cell) || cell.Length > 40)) return false;

        int cursor = index + 1;
        while (cursor < lines.Length && IsPipeTableRow(lines[cursor]))
        {
            string[] candidate = SplitMarkdownTableRow(lines[cursor]);
            if (candidate.Length != cells.Length) break;
            rows.Add(candidate);
            cursor++;
        }
        if (rows.Count > 0 && rows.SelectMany(row => row.Where((cell, cellIndex) => cell.Length > cells[cellIndex].Length || cell.Contains('\u2028'))).Any())
        {
            headers = cells;
            nextIndex = cursor;
            return true;
        }

        if (cells.Length >= 4 && cells.Length % 2 == 0)
        {
            int half = cells.Length / 2;
            string[] candidates = cells[..half];
            string[] values = cells[half..];
            if (candidates.All(cell => !string.IsNullOrWhiteSpace(cell) && cell.Length <= 40) &&
                values.Where((cell, cellIndex) => cell.Length > candidates[cellIndex].Length || cell.Contains('\u2028')).Any())
            {
                headers = candidates;
                rows = [values];
                nextIndex = index + 1;
                return true;
            }
        }
        rows.Clear();
        return false;
    }

    private void AddTable(string[] headers, IReadOnlyList<string[]> rows)
    {
        int columns = Math.Max(1, Math.Max(headers.Length, rows.Count == 0 ? 0 : rows.Max(row => row.Length)));
        var grid = new Grid
        {
            ColumnSpacing = 1,
            RowSpacing = 1,
            BackgroundColor = Color.FromArgb("#475569")
        };
        for (int column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int column = 0; column < columns; column++)
            AddTableCell(grid, column, 0, column < headers.Length ? headers[column] : "", true);
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            for (int column = 0; column < columns; column++)
                AddTableCell(grid, column, rowIndex + 1, column < rows[rowIndex].Length ? rows[rowIndex][column] : "", false);

        _blocks.Children.Add(new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Always,
            Content = grid
        });
    }

    private static void AddTableCell(Grid grid, int column, int row, string markdown, bool header)
    {
        Label label = CreateInlineLabel(markdown, 14, header ? FontAttributes.Bold : FontAttributes.None, Colors.White);
        var border = new Border
        {
            Padding = new Thickness(10, 8),
            MinimumWidthRequest = 120,
            MaximumWidthRequest = 360,
            StrokeThickness = 0,
            BackgroundColor = Color.FromArgb(header ? "#243247" : "#172033"),
            Content = label
        };
        Grid.SetColumn(border, column);
        Grid.SetRow(border, row);
        grid.Children.Add(border);
    }

    private void AddCodeBlock(string language, string code)
    {
        var codeLabel = new Label { Text = code, FontFamily = "monospace", FontSize = 13, TextColor = Color.FromArgb("#E2E8F0"), LineBreakMode = LineBreakMode.NoWrap };
        var stack = new VerticalStackLayout { Spacing = 5 };
        if (!string.IsNullOrWhiteSpace(language)) stack.Children.Add(new Label { Text = language.ToUpperInvariant(), FontSize = 10, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#94A3B8") });
        stack.Children.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = codeLabel });
        _blocks.Children.Add(new Border { Padding = 10, BackgroundColor = Color.FromArgb("#0F172A"), Stroke = Color.FromArgb("#334155"), StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 10 }, Content = stack });
    }
}
