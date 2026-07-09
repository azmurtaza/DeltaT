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
    // Notifies HasReason so the dial's tooltip is suppressed when there's nothing to say
    // (otherwise WPF pops an empty box on hover - the "sim card" glitch).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReason))]
    private string _topReason = "";

    /// <summary>True only when there's real reason text, so an empty tooltip never shows.</summary>
    public bool HasReason => !string.IsNullOrWhiteSpace(TopReason);
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

    public static (string Note, bool Active) DescribeFan(ComponentScore score)
    {
        ScoringEngine.FanNormalization? fan = score.Fan;
        if (fan is null)
            return ("", false);

        double c = fan.CorrectionC;
        if (Math.Abs(c) >= 1.0)
        {
            return ($"Fan-normalized {c:+0.#;-0.#}° - averaged {fan.RecentRpm:0} rpm vs a {fan.BaselineRpm:0} rpm baseline.", true);
        }

        return ($"Fans matched baseline ({fan.RecentRpm:0} rpm) - no airflow correction needed.", false);
    }
}
