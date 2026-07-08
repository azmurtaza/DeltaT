using CommunityToolkit.Mvvm.ComponentModel;
using DeltaT.Core.Scoring;

namespace DeltaT.App.ViewModels;

public partial class ScoreViewModel : ObservableObject
{
    public string Label { get; }

    [ObservableProperty] private int _value;
    [ObservableProperty] private bool _calibrating = true;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _verdictLabel = "learning";
    [ObservableProperty] private string _verdictShort = "learning";
    [ObservableProperty] private string _topReason = "";
    // While calibrating: the one thing holding the baseline back, so the meter explains itself.
    [ObservableProperty] private string _calibrationConstraint = "";
    // A pre-lock estimate: show the number, but marked as provisional with its confidence.
    [ObservableProperty] private bool _provisional;

    public ScoreViewModel(string label) => Label = label;

    public void Update(ComponentScore score)
    {
        Value = score.Value;
        Calibrating = score.Calibrating;
        Provisional = score.Provisional;
        Progress = score.CalibrationProgress;
        VerdictLabel = score.Verdict.Label();
        VerdictShort = score.Verdict.ShortLabel();
        TopReason = score.Reasons.Count > 0 ? score.Reasons[0].Text : "";
        CalibrationConstraint = score.CalibrationConstraint;
    }
}
