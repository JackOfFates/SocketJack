using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace heirowLLM;

public partial class MainWindow
{
    private bool _refreshingDreamModelChoices;
    private readonly HashSet<string> _availableDreamModelIds = new(StringComparer.OrdinalIgnoreCase);

    private void ApplyDreamModelSettingToProxy(heirowLLMSettings settings)
    {
        if (_proxy == null || settings == null)
            return;

        string model = NormalizeDreamModel(settings.DreamChecksAndBalancesModel);
        settings.DreamChecksAndBalancesModel = model;
        _proxy.SetDreamExecutionModelDiagnostics(model);
        UpdateDreamModelSelectionStatus(model);
    }

    private void RefreshDreamModelChoices(IEnumerable<string>? discoveredModels = null)
    {
        if (DreamChecksModelComboBox == null)
            return;

        string selected = DreamChecksModelComboBox.IsKeyboardFocusWithin || DreamChecksModelComboBox.IsDropDownOpen
            ? GetSelectedDreamChecksModel()
            : NormalizeDreamModel(_settings?.DreamChecksAndBalancesModel);
        var modelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (discoveredModels != null)
        {
            _availableDreamModelIds.Clear();
            foreach (string model in discoveredModels)
            {
                AddDreamModelChoice(modelIds, model);
                AddDreamModelChoice(_availableDreamModelIds, model);
            }
        }

        foreach (string model in _availableDreamModelIds)
            AddDreamModelChoice(modelIds, model);

        if (WpfChatModelComboBox?.ItemsSource is IEnumerable<string> chatModels)
        {
            foreach (string model in chatModels)
                AddDreamModelChoice(modelIds, model);
        }

        AddDreamModelChoice(modelIds, selected);
        List<DreamModelChoiceItem> choices = modelIds
            .Where(model => !model.Equals("auto", StringComparison.OrdinalIgnoreCase))
            .Select(model => new DreamModelChoiceItem(model, TryReadParameterBillions(model)))
            .OrderByDescending(choice => choice.IsRecommended)
            .ThenBy(choice => choice.ParameterBillions ?? double.MaxValue)
            .ThenBy(choice => choice.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        choices.Insert(0, DreamModelChoiceItem.Auto);

        _refreshingDreamModelChoices = true;
        try
        {
            DreamChecksModelComboBox.ItemsSource = choices;
            DreamModelChoiceItem? selectedChoice = choices.FirstOrDefault(choice =>
                string.Equals(choice.ModelId, selected, StringComparison.OrdinalIgnoreCase));
            DreamChecksModelComboBox.SelectedItem = selectedChoice;
            if (selectedChoice == null)
                DreamChecksModelComboBox.Text = selected;
            else
                DreamChecksModelComboBox.Text = selectedChoice.DisplayName;
        }
        finally
        {
            _refreshingDreamModelChoices = false;
        }

        UpdateDreamModelSelectionStatus(selected);
    }

    private static void AddDreamModelChoice(HashSet<string> models, string? model)
    {
        string value = (model ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(value))
            models.Add(value);
    }

    private string GetSelectedDreamChecksModel()
    {
        if (DreamChecksModelComboBox?.SelectedItem is DreamModelChoiceItem selected)
            return NormalizeDreamModel(selected.ModelId);

        return NormalizeDreamModel(FirstNonEmpty(
            DreamChecksModelComboBox?.Text,
            _settings?.DreamChecksAndBalancesModel,
            "auto"));
    }

    private void DreamChecksModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SaveDreamModelSelection();

    private void DreamChecksModelComboBox_LostFocus(object sender, RoutedEventArgs e) =>
        SaveDreamModelSelection();

    private void SaveDreamModelSelection()
    {
        if (_refreshingDreamModelChoices || _isApplyingSettings || _settings == null || _proxy == null)
            return;

        string model = GetSelectedDreamChecksModel();
        if (string.Equals(_settings.DreamChecksAndBalancesModel, model, StringComparison.OrdinalIgnoreCase))
        {
            UpdateDreamModelSelectionStatus(model);
            return;
        }

        _settings.DreamChecksAndBalancesModel = model;
        PersistSettingsToDisk(_settings, applyToServices: true);
        UpdateDreamModelSelectionStatus(model);
        if (SettingsStatusText != null)
            SettingsStatusText.Text = "Dream / Checks and Balances model saved: " + model;
    }

    private void UpdateDreamModelSelectionStatus(string model)
    {
        if (DreamChecksModelStatusText == null)
            return;

        string value = NormalizeDreamModel(model);
        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            DreamChecksModelStatusText.Text = "Auto may select a larger model; 1.5B-6B is recommended.";
            DreamChecksModelStatusText.Foreground = System.Windows.Media.Brushes.Goldenrod;
            return;
        }

        double? parameters = TryReadParameterBillions(value);
        if (IsRecommendedDreamModel(parameters))
        {
            DreamChecksModelStatusText.Text = "Recommended low-resource model (" + parameters!.Value.ToString("0.#", CultureInfo.InvariantCulture) + "B).";
            DreamChecksModelStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7C, 0xFF, 0x9D));
        }
        else
        {
            DreamChecksModelStatusText.Text = "For this GPU, a local 1.5B-6B model is recommended.";
            DreamChecksModelStatusText.Foreground = System.Windows.Media.Brushes.Goldenrod;
        }
    }

    private string[] BuildDreamModelOptionChoices()
    {
        if (DreamChecksModelComboBox?.ItemsSource is IEnumerable<DreamModelChoiceItem> choices)
            return choices.Select(choice => choice.ModelId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        return ["auto", NormalizeDreamModel(_settings?.DreamChecksAndBalancesModel)];
    }

    private static string NormalizeDreamModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? "auto" : model.Trim();

    internal static double? TryReadParameterBillions(string? model)
    {
        Match match = Regex.Match(model ?? "", @"(?<![0-9.])(?<size>\d+(?:\.\d+)?)\s*B(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && double.TryParse(match.Groups["size"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    internal static bool IsRecommendedDreamModel(double? parameterBillions) =>
        parameterBillions is >= 1.5 and <= 6.0;

    private sealed class DreamModelChoiceItem
    {
        public static readonly DreamModelChoiceItem Auto = new("auto", null);

        public DreamModelChoiceItem(string modelId, double? parameterBillions)
        {
            ModelId = modelId;
            ParameterBillions = parameterBillions;
            IsRecommended = IsRecommendedDreamModel(parameterBillions);
            DisplayName = ModelId.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? "Auto (runtime chooses)"
                : IsRecommended
                    ? "Recommended - " + ModelId + " - " + parameterBillions!.Value.ToString("0.#", CultureInfo.InvariantCulture) + "B low resource"
                    : ModelId;
        }

        public string ModelId { get; }
        public double? ParameterBillions { get; }
        public bool IsRecommended { get; }
        public string DisplayName { get; }

        public override string ToString() => DisplayName;
    }
}
