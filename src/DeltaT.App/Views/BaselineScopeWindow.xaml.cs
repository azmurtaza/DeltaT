using System.Windows;
using DeltaT.Core.Monitoring;

namespace DeltaT.App.Views;

/// <summary>Asks which components a repaste or recalibration actually applies to. CPU and GPU
/// learn, lock and score independently, so resetting both when only one was touched throws away
/// a hard-earned reference for nothing (relearning takes days of real load). Skipped entirely on
/// a machine with only one pasted component: there is no choice to make there.</summary>
public partial class BaselineScopeWindow : Window
{
    /// <summary>Internal rather than private so the --uishot harness can render it offscreen;
    /// callers go through <see cref="Ask"/>.</summary>
    internal BaselineScopeWindow(bool repaste)
    {
        InitializeComponent();

        Overline.Text = repaste ? "LOG A REPASTE" : "RECALIBRATE";
        Heading.Text = repaste ? "What did you repaste?" : "What should DeltaT recalibrate?";
        ConfirmButton.Content = repaste ? "LOG REPASTE" : "RECALIBRATE";

        Blurb.Text = repaste
            ? "The components you name start learning a fresh baseline, and DeltaT reports what the new paste bought once that baseline locks."
            : "The components you name are re-checked against their old baseline under real load. If nothing actually changed, the old reference is kept and scoring resumes within the hour.";

        BothLabel.Text = "CPU and GPU";
        BothHint.Text = repaste
            ? "Both got fresh paste."
            : "The change affects the whole machine, a clean-out or new fans, for example.";

        CpuLabel.Text = "CPU only";
        CpuHint.Text = repaste
            ? "The GPU keeps its current baseline and score."
            : "Only the CPU is re-checked. The GPU keeps its current baseline and score.";

        GpuLabel.Text = "GPU only";
        GpuHint.Text = repaste
            ? "The CPU keeps its current baseline and score."
            : "Only the GPU is re-checked. The CPU keeps its current baseline and score.";
    }

    /// <summary>The chosen scope, or null if the user backed out.</summary>
    public IReadOnlyList<ComponentKind>? Scope { get; private set; }

    /// <summary>Shows the picker and returns the chosen components, or null if the user backed
    /// out. Call only when the machine actually has both pasted components; with one there is
    /// nothing to choose and the plain confirmation belongs instead.</summary>
    public static IReadOnlyList<ComponentKind>? Ask(Window? owner, bool repaste)
    {
        var window = new BaselineScopeWindow(repaste) { Owner = owner };
        window.ShowDialog();
        return window.Scope;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Scope = ScopeCpu.IsChecked == true ? new[] { ComponentKind.Cpu }
            : ScopeGpu.IsChecked == true ? new[] { ComponentKind.GpuDiscrete }
            : new[] { ComponentKind.Cpu, ComponentKind.GpuDiscrete };
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Scope = null;
        Close();
    }
}
