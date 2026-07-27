using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace DeltaT.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    private void OnRunFingerprint(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).OpenFingerprintWindow();

    // ---------------------------------------------------------------- hero readout pager
    //
    // The hero's instrument strip is one row by design: side by side, the readings read as a
    // measurement chain (rise measured, watts corrected, airflow corrected, limit hit). The
    // strip gains a readout when the machine throttles or is still calibrating, so on a
    // narrow window it can outrun the column. Wrapping it would push the cause chip down and
    // break the chain's read, so it pages sideways one instrument at a time instead. At full
    // screen everything fits, ScrollableWidth is 0, and the pager hides itself entirely.

    private int _heroStatCount = -1;

    private void OnHeroStatsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // A rebuilt strip is a new set of readings, and it may have gained or lost one
        // (a throttle appearing, a baseline locking). Start it at the first instrument
        // rather than wherever the previous set happened to be paged to.
        if (HeroStatScroller.Content is ItemsControl items && items.Items.Count != _heroStatCount)
        {
            _heroStatCount = items.Items.Count;
            HeroStatScroller.ScrollToHome();
        }
        UpdateHeroPager();
    }

    /// <summary>Only the pager moves the strip. Left to itself, a readout requesting to be
    /// brought into view scrolls the row out from under whatever the user was reading.</summary>
    private void OnHeroStatsBringIntoView(object sender, RequestBringIntoViewEventArgs e) => e.Handled = true;

    private void OnHeroStatsPrev(object sender, RoutedEventArgs e) => PageHeroStats(forward: false);

    private void OnHeroStatsNext(object sender, RoutedEventArgs e) => PageHeroStats(forward: true);

    /// <summary>Scroll to the next (or previous) readout boundary, so a page always lands on
    /// a whole instrument rather than slicing one down the middle.</summary>
    private void PageHeroStats(bool forward)
    {
        ScrollViewer sv = HeroStatScroller;
        double current = sv.HorizontalOffset;
        double target = forward ? sv.ScrollableWidth : 0;

        foreach (double edge in HeroStatEdges())
        {
            if (forward && edge > current + 1)
            {
                target = Math.Min(edge, sv.ScrollableWidth);
                break;
            }
            if (!forward && edge < current - 1)
                target = edge; // keep the LAST edge before the current offset
        }
        AnimateHeroScroll(target);
    }

    /// <summary>Left edge of every readout, in the strip's own coordinates.</summary>
    private IEnumerable<double> HeroStatEdges()
    {
        if (HeroStatScroller.Content is not ItemsControl items)
            yield break;
        for (int i = 0; i < items.Items.Count; i++)
        {
            if (items.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement el || !el.IsVisible)
                continue;
            yield return el.TranslatePoint(new Point(0, 0), items).X;
        }
    }

    /// <summary>Glide rather than jump: the strip is a physical instrument row, and a
    /// teleporting readout reads as a value change instead of a viewport move.</summary>
    private void AnimateHeroScroll(double target)
    {
        var anim = new DoubleAnimation(HeroStatScroller.HorizontalOffset, target, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(HeroScrollOffsetProperty, null);
        BeginAnimation(HeroScrollOffsetProperty, anim);
    }

    /// <summary>ScrollViewer.HorizontalOffset is read-only, so the animation drives this
    /// proxy property and each tick is forwarded to ScrollToHorizontalOffset.</summary>
    private static readonly DependencyProperty HeroScrollOffsetProperty = DependencyProperty.Register(
        "HeroScrollOffset", typeof(double), typeof(DashboardView),
        new PropertyMetadata(0.0, (d, e) =>
            ((DashboardView)d).HeroStatScroller.ScrollToHorizontalOffset((double)e.NewValue)));

    /// <summary>Show the pager and the edge fade only while there is something off-screen,
    /// and grey each arrow out at its end of the strip.</summary>
    private void UpdateHeroPager()
    {
        ScrollViewer sv = HeroStatScroller;
        bool overflows = sv.ScrollableWidth > 1;
        HeroStatPager.Visibility = overflows ? Visibility.Visible : Visibility.Collapsed;
        HeroStatPrev.IsEnabled = overflows && sv.HorizontalOffset > 1;
        HeroStatNext.IsEnabled = overflows && sv.HorizontalOffset < sv.ScrollableWidth - 1;
        HeroStatFade.Visibility = HeroStatNext.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
    }
}
