using System;

namespace LlmRuntime.VisualStudio;

internal static class LlmRuntimeCommandIds
{
    public const string CommandSetString = "de8da57b-c53f-444e-90ed-b7c5ba148796";
    public static readonly Guid CommandSet = new(CommandSetString);

    public const int Chat = 0x0100;
    public const int InlineCompletion = 0x0101;
    public const int NextEdit = 0x0102;
    public const int Agent = 0x0103;
    public const int Checkpoint = 0x0104;
    public const int Mcp = 0x0105;
    public const int ModelPicker = 0x0106;
    public const int CodeReview = 0x0107;
    public const int WorkspaceIndex = 0x0108;
    public const int PromptFiles = 0x0109;
    public const int CustomInstructions = 0x010A;
    public const int CopilotServers = 0x010B;
    public const int CreateMcpConfig = 0x010C;
}
