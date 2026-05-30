using System.Windows;
using LlmRuntime;

namespace LlmRuntime.Wpf;

public partial class ToolDefinitionsWindow : Window
{
    public ToolDefinitionsWindow()
    {
        InitializeComponent();
    }

    public ToolDefinitionsWindow(LlmRuntimeOptions options) : this()
    {
        ToolDefinitions.Configure(options);
    }

    public LlmToolRegistry Registry
    {
        get => ToolDefinitions.Registry;
        set => ToolDefinitions.Registry = value;
    }
}
