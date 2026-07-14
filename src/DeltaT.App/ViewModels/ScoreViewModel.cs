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
    // Locked, but nothing comparable measured yet: the dial shows "--", not a hollow 100.
    [ObservableProperty] private bool _awaitingData;

    public ScoreViewModel(string label) => Label = label;

    // A backward-sliding meter reads as "broken/random", so the displayed calibration
    // progress only ever climbs within an epoch. A large drop means a new epoch started
    // (repaste/recalibrate reset the window), so we follow it back down and re-climb.
    private double _progressFloor;
    private const double EpochResetDrop = 0.15;

    public void Update(ComponentScore score)
    {
        Value = score.Value;
        Calibrating = score.Calibrating;
        Provisional = score.Provisional;
        AwaitingData = score.AwaitingData;

        double incoming = score.CalibrationProgress;
        if (!score.Calibrating)
            _progressFloor = 0;                                  // locked: meter retired, reset for a future epoch
        else if (incoming < _progressFloor - EpochResetDrop)
            _progressFloor = incoming;                           // new epoch — restart the climb
        else
            _progressFloor = Math.Max(_progressFloor, incoming); // otherwise never regress
        Progress = score.Calibrating ? _progressFloor : incoming;

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
            return ($"Fan-normalized {c:+0.#;-0.#}°, averaging {fan.RecentRpm:0} rpm against a {fan.BaselineRpm:0} rpm baseline.", true);
        }

        return ($"Fans matched baseline ({fan.RecentRpm:0} rpm), no airflow correction needed.", false);
    }
}
