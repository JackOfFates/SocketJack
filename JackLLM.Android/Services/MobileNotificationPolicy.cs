namespace JackLLM.Mobile.Services;

public sealed class MobileResponseMilestonePolicy
{
    public bool HasReachedVisibleAnswer { get; private set; }

    public void Reset() => HasReachedVisibleAnswer = false;

    public bool TryReachVisibleAnswer(bool hasSanitizedVisibleContent)
    {
        if (HasReachedVisibleAnswer || !hasSanitizedVisibleContent) return false;
        HasReachedVisibleAnswer = true;
        return true;
    }
}

public static class MobileNotificationText
{
    public static int IncrementUnread(int current) => Math.Max(0, current) + 1;
    public static string FormatUnread(int unread) => $"JackLLM has {Math.Max(0, unread)} new notifications.";
}
