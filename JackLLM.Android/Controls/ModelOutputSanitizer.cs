using System.Text;
using System.Text.RegularExpressions;

namespace JackLLM.Mobile.Controls;

public static class ModelOutputSanitizer
{
    private const string ProtocolNames = "solution|output|message|session|assistant|user|system|response|result|completion|content|root|model|final|answer|analysis|reasoning|thinking|think|tool_response|function_response";
    private static readonly Regex CompleteProtocolTag = new($@"</?(?:{ProtocolNames})(?:\s[^<>]*?)?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingProtocolTag = new($@"</?(?:{ProtocolNames})[^<>]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnyMarkupTag = new(@"</?[A-Za-z_][A-Za-z0-9_.:-]*(?:\s[^<>]*?)?/?>", RegexOptions.Compiled);
    private static readonly Regex TrailingMarkupTag = new(@"</?[A-Za-z_][A-Za-z0-9_.:-]*(?:\s[^<>]*)?$", RegexOptions.Compiled);
    private static readonly Regex DanglingMarkupStart = new(@"</?\s*$", RegexOptions.Compiled);
    private static readonly Regex ChatMlToken = new(@"<\|[^|<>\r\n]+\|>", RegexOptions.Compiled);
    private static readonly Regex InstructionToken = new(@"\[/?INST\]|</?s>|<bos>|<eos>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlLineBreak = new(@"(?:<|&lt;)br\s*/?(?:>|&gt;)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExcessBlankLines = new(@"\n[ \t]*\n(?:[ \t]*\n)+", RegexOptions.Compiled);

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        var output = new StringBuilder(normalized.Length);
        bool inFence = false;
        bool inToolBlock = false;
        foreach (string sourceLine in lines)
        {
            string line = sourceLine;
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                AppendLine(output, line);
                continue;
            }
            if (!inFence)
            {
                // Preserve safe formatting semantics before stripping arbitrary
                // model-generated HTML. U+2028 keeps a table row intact while
                // still rendering as a line break inside a MAUI Label.
                line = HtmlLineBreak.Replace(line, "\u2028");
                if (inToolBlock)
                {
                    int toolEnd = line.IndexOf("</tool_call>", StringComparison.OrdinalIgnoreCase);
                    if (toolEnd < 0) continue;
                    line = line[(toolEnd + 12)..];
                    inToolBlock = false;
                }
                int toolStart;
                while ((toolStart = line.IndexOf("<tool_call", StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    int toolEnd = line.IndexOf("</tool_call>", toolStart, StringComparison.OrdinalIgnoreCase);
                    if (toolEnd < 0)
                    {
                        line = line[..toolStart];
                        inToolBlock = true;
                        break;
                    }
                    line = line.Remove(toolStart, toolEnd + 12 - toolStart);
                }
                line = CompleteProtocolTag.Replace(line, "");
                line = ChatMlToken.Replace(line, "");
                line = InstructionToken.Replace(line, "");
                line = TrailingProtocolTag.Replace(line, "");
                line = AnyMarkupTag.Replace(line, "");
                line = TrailingMarkupTag.Replace(line, "");
                line = DanglingMarkupStart.Replace(line, "");
            }
            AppendLine(output, line);
        }
        string cleaned = output.ToString().TrimEnd('\n');
        return ExcessBlankLines.Replace(cleaned, "\n\n").Trim();
    }

    private static void AppendLine(StringBuilder output, string line)
    {
        if (output.Length > 0) output.Append('\n');
        output.Append(line);
    }
}
