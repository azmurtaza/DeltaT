using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace DeltaT.App.Services;

/// <summary>Renders a QR code to a WPF image, entirely in-process (QRCoder is pure managed, no
/// native dependency, no network). Kept black-on-white at whatever module size is asked for,
/// because a payment QR must scan reliably and high contrast is what does that; the surrounding
/// card supplies the DeltaT styling.</summary>
public static class QrImage
{
    public static BitmapSource Generate(string text, int pixelsPerModule = 8)
    {
        var generator = new QRCodeGenerator();
        QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        byte[] png = new PngByteQRCode(data).GetGraphic(pixelsPerModule);

        var bmp = new BitmapImage();
        using var ms = new MemoryStream(png);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze(); // cross-thread safe and immutable
        return bmp;
    }
}
