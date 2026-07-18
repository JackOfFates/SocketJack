namespace JackLLM.Mobile.Services;

/// <summary>
/// Keeps replayed stream frames and exact model repetition from being shown as
/// additional answer text. The thresholds are deliberately large enough that
/// normal repeated words and punctuation remain untouched.
/// </summary>
public static class MobileOutputReliability
{
    private const int MinimumReplayLength = 24;
    private const int MinimumRepeatedBlockLength = 80;

    public static string MergeStreamDelta(string existing, string incoming)
    {
        existing ??= "";
        incoming ??= "";
        if (incoming.Length == 0) return existing;
        if (existing.Length == 0) return CollapseExactAdjacentBlocks(incoming);

        string merged;
        if (existing.Length >= MinimumReplayLength &&
            incoming.Length > existing.Length &&
            incoming.StartsWith(existing, StringComparison.Ordinal))
        {
            // Some adapters send the answer-so-far in each content frame.
            merged = incoming;
        }
        else if (incoming.Length >= MinimumReplayLength &&
                 existing.EndsWith(incoming, StringComparison.Ordinal))
        {
            // Ignore a frame that was already appended (for example after a
            // reconnect or an upstream retry).
            merged = existing;
        }
        else
        {
            int overlap = FindSuffixPrefixOverlap(existing, incoming);
            merged = overlap >= MinimumReplayLength
                ? existing + incoming[overlap..]
                : existing + incoming;
        }

        return CollapseExactAdjacentBlocks(merged);
    }

    public static string CollapseExactAdjacentBlocks(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < MinimumRepeatedBlockLength * 2)
            return value ?? "";

        string result = value;
        bool changed;
        do
        {
            changed = TryCollapseRepeatedLines(ref result) || TryCollapseWholeTextRepeat(ref result);
        }
        while (changed);
        return result;
    }

    private static int FindSuffixPrefixOverlap(string existing, string incoming)
    {
        int maximum = Math.Min(existing.Length, incoming.Length);
        for (int length = maximum; length >= MinimumReplayLength; length--)
        {
            if (existing.AsSpan(existing.Length - length).SequenceEqual(incoming.AsSpan(0, length)))
                return length;
        }
        return 0;
    }

    private static bool TryCollapseRepeatedLines(ref string value)
    {
        string newline = value.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 2) return false;

        for (int blockLines = lines.Length / 2; blockLines >= 1; blockLines--)
        {
            for (int start = 0; start + (blockLines * 2) <= lines.Length; start++)
            {
                for (int gap = 0; gap <= 2; gap++)
                {
                    int secondStart = start + blockLines + gap;
                    if (secondStart + blockLines > lines.Length) continue;
                    if (gap > 0 && lines.Skip(start + blockLines).Take(gap).Any(line => !string.IsNullOrWhiteSpace(line))) continue;

                    bool equal = true;
                    for (int offset = 0; offset < blockLines; offset++)
                    {
                        if (!string.Equals(lines[start + offset].TrimEnd(), lines[secondStart + offset].TrimEnd(), StringComparison.Ordinal))
                        {
                            equal = false;
                            break;
                        }
                    }
                    if (!equal) continue;

                    int blockLength = 0;
                    for (int offset = 0; offset < blockLines; offset++) blockLength += lines[start + offset].Length + 1;
                    if (blockLength < MinimumRepeatedBlockLength) continue;

                    var kept = new List<string>(lines.Length - blockLines);
                    kept.AddRange(lines.Take(secondStart));
                    kept.AddRange(lines.Skip(secondStart + blockLines));
                    value = string.Join(newline, kept);
                    return true;
                }
            }
        }
        return false;
    }

    private static bool TryCollapseWholeTextRepeat(ref string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length < MinimumRepeatedBlockLength * 2) return false;

        for (int copies = Math.Min(8, trimmed.Length / MinimumRepeatedBlockLength); copies >= 2; copies--)
        {
            if (trimmed.Length % copies != 0) continue;
            int blockLength = trimmed.Length / copies;
            ReadOnlySpan<char> block = trimmed.AsSpan(0, blockLength);
            bool equal = true;
            for (int copy = 1; copy < copies; copy++)
            {
                if (!block.SequenceEqual(trimmed.AsSpan(copy * blockLength, blockLength)))
                {
                    equal = false;
                    break;
                }
            }
            if (!equal) continue;
            value = trimmed[..blockLength].TrimEnd();
            return true;
        }
        return false;
    }
}
