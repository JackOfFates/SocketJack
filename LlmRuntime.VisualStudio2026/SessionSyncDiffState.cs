namespace LlmRuntime.VisualStudio2026;

internal static class SessionSyncDiffState
{
    public static bool KnownHashesEqual(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasLocalBaseline(DateTimeOffset localLastWriteUtc, long localSizeBytes, string localSha256)
    {
        return localLastWriteUtc > DateTimeOffset.MinValue ||
            localSizeBytes > 0 ||
            !string.IsNullOrWhiteSpace(localSha256);
    }

    public static bool HasRemoteBaseline(DateTimeOffset remoteLastWriteUtc, long remoteSizeBytes, string remoteSha256)
    {
        return remoteLastWriteUtc > DateTimeOffset.MinValue ||
            remoteSizeBytes > 0 ||
            !string.IsNullOrWhiteSpace(remoteSha256);
    }

    public static bool IsRemoteOnly(
        DateTimeOffset localLastWriteUtc,
        long localSizeBytes,
        string localSha256,
        DateTimeOffset remoteLastWriteUtc,
        long remoteSizeBytes,
        string remoteSha256)
    {
        return HasRemoteBaseline(remoteLastWriteUtc, remoteSizeBytes, remoteSha256) &&
            !HasLocalBaseline(localLastWriteUtc, localSizeBytes, localSha256);
    }

    public static bool RemoteMetadataChanged(DateTimeOffset previousUtc, DateTimeOffset nextUtc, long previousSizeBytes, long nextSizeBytes)
    {
        bool timeChanged = previousUtc > DateTimeOffset.MinValue &&
            nextUtc > DateTimeOffset.MinValue &&
            Math.Abs((nextUtc - previousUtc).TotalMilliseconds) > 999;
        bool sizeChanged = previousSizeBytes > 0 &&
            nextSizeBytes > 0 &&
            previousSizeBytes != nextSizeBytes;
        return timeChanged || sizeChanged;
    }

    public static bool LocalMetadataChanged(
        DateTimeOffset previousUtc,
        long previousSizeBytes,
        string previousSha256,
        DateTimeOffset nextUtc,
        long nextSizeBytes,
        string nextSha256)
    {
        if (!HasLocalBaseline(previousUtc, previousSizeBytes, previousSha256))
        {
            return true;
        }

        if (KnownHashesEqual(previousSha256, nextSha256))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(previousSha256) && !string.IsNullOrWhiteSpace(nextSha256))
        {
            return true;
        }

        bool timeChanged = previousUtc > DateTimeOffset.MinValue &&
            nextUtc > DateTimeOffset.MinValue &&
            Math.Abs((nextUtc - previousUtc).TotalMilliseconds) > 999;
        bool sizeChanged = previousSizeBytes != nextSizeBytes;
        return timeChanged || sizeChanged;
    }

    public static bool HasUnpushedLocalChange(
        bool localExists,
        DateTimeOffset previousUtc,
        long previousSizeBytes,
        string previousSha256,
        DateTimeOffset nextUtc,
        long nextSizeBytes,
        string nextSha256)
    {
        if (!localExists)
        {
            return HasLocalBaseline(previousUtc, previousSizeBytes, previousSha256);
        }

        return LocalMetadataChanged(previousUtc, previousSizeBytes, previousSha256, nextUtc, nextSizeBytes, nextSha256);
    }
}
