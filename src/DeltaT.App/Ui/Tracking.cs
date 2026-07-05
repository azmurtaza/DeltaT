using System.Windows;
using System.Windows.Controls;

namespace DeltaT.App.Ui;

/// <summary>WPF has no letterspacing, so overline labels fake it: set
/// <c>ui:Tracking.Text</c> instead of <c>Text</c> and the string is upper-cased
/// with hair spaces (U+200A) interleaved. Proportional faces only, never mono,
/// where the fake tracking would fight the grid.</summary>
public static class Tracking
{
    private const char HairSpace = (char)0x200A;

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Tracking), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock tb)
            tb.Text = Spread(e.NewValue as string);
    }

    private static string Spread(string? s) =>
        string.IsNullOrEmpty(s) ? "" : string.Join(HairSpace.ToString(), s.ToUpperInvariant().ToCharArray());
}
