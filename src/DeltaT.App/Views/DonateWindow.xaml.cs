using System.Windows;
using DeltaT.App.Services;

namespace DeltaT.App.Views;

public partial class DonateWindow : Window
{
    public DonateWindow()
    {
        InitializeComponent();
        AddressText.Text = Donation.UsdtTrc20Address;
        UidText.Text = Donation.BinanceUid;
        // Encode the Binance Pay link: scanning it opens Binance Pay to tip with any coin.
        QrCode.Source = QrImage.Generate(Donation.BinancePayLink, pixelsPerModule: 8);
    }

    private void OnCopyAddress(object sender, RoutedEventArgs e) =>
        Copy(Donation.UsdtTrc20Address, "USDT (TRC20) address copied.");

    private void OnCopyUid(object sender, RoutedEventArgs e) =>
        Copy(Donation.BinanceUid, "Binance UID copied.");

    private void Copy(string text, string confirm)
    {
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = confirm;
        }
        catch
        {
            // The clipboard can be locked by another app for a moment; say so rather than throw.
            StatusText.Text = "Couldn't reach the clipboard just now. Try again.";
        }
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnCloseChrome(object sender, RoutedEventArgs e) => Close();
}
