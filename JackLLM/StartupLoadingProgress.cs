namespace JackLLM;

public sealed class StartupLoadingProgress {
    public StartupLoadingProgress(double value, string message, string detail = "", bool isIndeterminate = false) {
        Value = value;
        Message = message ?? "";
        Detail = detail ?? "";
        IsIndeterminate = isIndeterminate;
    }

    public double Value { get; }
    public string Message { get; }
    public string Detail { get; }
    public bool IsIndeterminate { get; }
}
