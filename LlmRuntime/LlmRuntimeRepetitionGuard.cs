using System.Text;

namespace LlmRuntime;

internal sealed class LlmRuntimeRepetitionGuard
{
    private const int RepeatThreshold = 4;
    private const int MinimumSegmentLength = 48;
    private const int MinimumSegmentWords = 6;
    private const int MinimumCharactersBeforeStop = 256;

    private readonly StringBuilder _accepted = new();

    public LlmRuntimeRepetitionGuardDecision Accept(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new LlmRuntimeRepetitionGuardDecision("", false, null);

        int previousLength = _accepted.Length;
        string combined = _accepted + text;
        if (TryFindRepetitionStopIndex(combined, out int stopIndex, out string? reason))
        {
            int safeLength = Math.Max(0, Math.Min(stopIndex, combined.Length) - previousLength);
            string safeText = safeLength > 0 ? combined.Substring(previousLength, safeLength) : "";
            _accepted.Clear();
            _accepted.Append(combined, 0, Math.Clamp(stopIndex, 0, combined.Length));
            return new LlmRuntimeRepetitionGuardDecision(safeText, true, reason);
        }

        _accepted.Append(text);
        return new LlmRuntimeRepetitionGuardDecision(text, false, null);
    }

    public static string TrimRepeatingTail(string text)
    {
        if (!TryFindRepetitionStopIndex(text, out int stopIndex, out _))
            return text;

        return text[..stopIndex].TrimEnd();
    }

    private static bool TryFindRepetitionStopIndex(string text, out int stopIndex, out string? reason)
    {
        stopIndex = -1;
        reason = null;

        if (string.IsNullOrEmpty(text) || text.Length < MinimumCharactersBeforeStop)
            return false;

        if (TryFindRepeatedSegment(text, EnumerateLineSegments, "line", out stopIndex, out reason))
            return true;

        return TryFindRepeatedSegment(text, EnumerateSentenceSegments, "sentence", out stopIndex, out reason);
    }

    private static bool TryFindRepeatedSegment(
        string text,
        Func<string, IEnumerable<LlmRuntimeTextSegment>> enumerateSegments,
        string segmentKind,
        out int stopIndex,
        out string? reason)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        stopIndex = -1;
        reason = null;

        foreach (LlmRuntimeTextSegment segment in enumerateSegments(text))
        {
            if (!IsRepeatedCandidate(segment.Normalized))
                continue;

            counts.TryGetValue(segment.Normalized, out int count);
            count++;
            counts[segment.Normalized] = count;

            if (count >= RepeatThreshold && segment.Start >= MinimumCharactersBeforeStop)
            {
                stopIndex = segment.Start;
                reason = "repeated_" + segmentKind;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<LlmRuntimeTextSegment> EnumerateLineSegments(string text)
    {
        int start = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
                continue;

            int end = index > start && text[index - 1] == '\r' ? index - 1 : index;
            if (end > start)
                yield return CreateSegment(text, start, end);
            start = index + 1;
        }

        if (start < text.Length)
            yield return CreateSegment(text, start, text.Length);
    }

    private static IEnumerable<LlmRuntimeTextSegment> EnumerateSentenceSegments(string text)
    {
        int start = 0;
        for (int index = 0; index < text.Length; index++)
        {
            char ch = text[index];
            if (ch != '.' && ch != '!' && ch != '?' && ch != '\n')
                continue;

            int end = ch == '\n' && index > start && text[index - 1] == '\r' ? index - 1 : index + 1;
            if (end > start)
                yield return CreateSegment(text, start, end);
            start = index + 1;
        }

        if (start < text.Length)
            yield return CreateSegment(text, start, text.Length);
    }

    private static LlmRuntimeTextSegment CreateSegment(string text, int start, int end) =>
        new(start, end, NormalizeSegment(text.AsSpan(start, end - start)));

    private static string NormalizeSegment(ReadOnlySpan<char> text)
    {
        var builder = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (char.IsControl(ch))
                continue;

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString().Trim();
    }

    private static bool IsRepeatedCandidate(string normalized)
    {
        if (normalized.Length < MinimumSegmentLength)
            return false;

        int words = 0;
        var uniqueWords = new HashSet<string>(StringComparer.Ordinal);
        foreach (string rawWord in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string word = TrimWord(rawWord);
            if (word.Length < 3)
                continue;

            words++;
            uniqueWords.Add(word);
        }

        return words >= MinimumSegmentWords && uniqueWords.Count >= 4;
    }

    private static string TrimWord(string word)
    {
        int start = 0;
        int end = word.Length - 1;

        while (start <= end && !char.IsLetterOrDigit(word[start]))
            start++;

        while (end >= start && !char.IsLetterOrDigit(word[end]))
            end--;

        return start <= end ? word[start..(end + 1)] : "";
    }
}

internal readonly record struct LlmRuntimeRepetitionGuardDecision(string Text, bool ShouldStop, string? Reason);

internal readonly record struct LlmRuntimeTextSegment(int Start, int End, string Normalized);
