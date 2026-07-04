using CommunityToolkit.Mvvm.ComponentModel;
using Kelvin.Core.Scoring;

namespace Kelvin.App.ViewModels;

public partial class ScoreViewModel : ObservableObject
{
    public string Label { get; }

    [ObservableProperty] private int _value;
    [ObservableProperty] private bool _calibrating = true;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _verdictLabel = "learning";
    [ObservableProperty] private string _topReason = "";

    public ScoreViewModel(string label) => Label = label;

    public void Update(ComponentScore score)
    {
        Value = score.Value;
        Calibrating = score.Calibrating;
        Progress = score.CalibrationProgress;
        VerdictLabel = score.Verdict.Label();
        TopReason = score.Reasons.Count > 0 ? score.Reasons[0].Text : "";
    }
}
