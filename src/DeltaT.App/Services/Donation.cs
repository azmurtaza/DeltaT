namespace DeltaT.App.Services;

/// <summary>The maintainer's donation details, shown in the in-app support panel. Static because
/// they are the developer's own fixed addresses, not per-user data. Update here if they change.
///
/// Receiving is display-only: a personal Binance account takes USDT to the TRC20 address and Pay
/// transfers to the UID without any merchant upgrade, but there is no way to auto-detect an
/// incoming personal transfer, so the panel only shows how to give, it never claims a total.</summary>
public static class Donation
{
    public const string Title = "Support DeltaT";

    /// <summary>Binance Pay universal QR link. Scanning it opens Binance Pay to tip the
    /// maintainer with any coin, no network to choose and no gas fee, so this is the primary
    /// method and what the QR encodes.</summary>
    public const string BinancePayLink = "https://app.binance.com/uni-qr/BV5nKqME";

    /// <summary>Binance user ID, for a Binance Pay transfer by hand when scanning isn't handy.</summary>
    public const string BinanceUid = "499330351";

    /// <summary>USDT on the TRON network: the fallback for donors not on Binance (any wallet or
    /// exchange can send USDT-TRC20 to this address).</summary>
    public const string UsdtTrc20Address = "TGe7X7bSXUzsw1hzfcUCq1vVJGtsMzLVjg";
    public const string Network = "USDT · TRON (TRC20)";
}
