using System.Text;

namespace LlmRuntime;

internal sealed class LlmHiddenReasoningStreamFilter
{
    private static readonly string[] OpenTags = ["<think>", "<thinking>", "<thought>", "<analysis>"];
    private static readonly string[] CloseTags = ["</think>", "</thinking>", "</thought>", "</analysis>"];

    private readonly StringBuilder _pending = new();
    private bool _insideHiddenReasoning;

    public string Accept(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        _pending.Append(text);
        return Drain(final: false);
    }

    public string Flush() => Drain(final: true);

    private string Drain(bool final)
    {
        var visible = new StringBuilder();
        while (_pending.Length > 0)
        {
            if (_insideHiddenReasoning)
            {
                int closeIndex = FindEarliestTag(_pending, CloseTags, out int closeLength);
                if (closeIndex >= 0)
                {
                    _pending.Remove(0, closeIndex + closeLength);
                    _insideHiddenReasoning = false;
                    continue;
                }

                if (final)
                {
                    _pending.Clear();
                    break;
                }

                int keep = LongestTagPrefixSuffixLength(_pending, CloseTags);
                if (_pending.Length > keep)
                    _pending.Remove(0, _pending.Length - keep);
                break;
            }

            int openIndex = FindEarliestTag(_pending, OpenTags, out int openLength);
            if (openIndex >= 0)
            {
                if (openIndex > 0)
                {
                    visible.Append(_pending.ToString(0, openIndex));
                    _pending.Remove(0, openIndex);
                }

                _pending.Remove(0, openLength);
                _insideHiddenReasoning = true;
                continue;
            }

            int hold = final ? 0 : LongestTagPrefixSuffixLength(_pending, OpenTags);
            int emitLength = _pending.Length - hold;
            if (emitLength > 0)
            {
                visible.Append(_pending.ToString(0, emitLength));
                _pending.Remove(0, emitLength);
            }

            break;
        }

        return visible.ToString();
    }

    private static int FindEarliestTag(StringBuilder text, IReadOnlyList<string> tags, out int tagLength)
    {
        string value = text.ToString();
        int bestIndex = -1;
        tagLength = 0;
        foreach (string tag in tags)
        {
            int index = value.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (bestIndex < 0 || index < bestIndex))
            {
                bestIndex = index;
                tagLength = tag.Length;
            }
        }

        return bestIndex;
    }

    private static int LongestTagPrefixSuffixLength(StringBuilder text, IReadOnlyList<string> tags)
    {
        int maxLength = 0;
        int length = text.Length;
        foreach (string tag in tags)
        {
            int maxCandidate = Math.Min(length, tag.Length - 1);
            for (int candidate = maxCandidate; candidate > maxLength; candidate--)
            {
                if (EndsWithTagPrefix(text, tag, candidate))
                    maxLength = candidate;
            }
        }

        return maxLength;
    }

    private static bool EndsWithTagPrefix(StringBuilder text, string tag, int prefixLength)
    {
        if (prefixLength <= 0 || prefixLength > text.Length || prefixLength >= tag.Length)
            return false;

        int offset = text.Length - prefixLength;
        for (int i = 0; i < prefixLength; i++)
        {
            if (char.ToUpperInvariant(text[offset + i]) != char.ToUpperInvariant(tag[i]))
                return false;
        }

        return true;
    }
}
