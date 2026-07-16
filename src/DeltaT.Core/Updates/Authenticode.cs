using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace DeltaT.Core.Updates;

/// <summary>Authenticode verification for a downloaded executable, via WinVerifyTrust:
/// the same check Windows itself runs when you launch a file, and the same standard
/// DeltaT's installer already holds the PawnIO setup to before executing it.
///
/// Two questions, deliberately separate, because passing one without the other is
/// worthless:
///   1. Is the signature cryptographically valid and chained to a trusted root
///      (<see cref="IsTrusted"/>)? Proves the bytes weren't altered after signing.
///   2. Is the signer who we expect (<see cref="SignerSubject"/>)? Proves it was signed
///      by us and not by someone else's perfectly valid certificate.
/// An attacker can always sign malware with their own valid cert, so (1) alone proves
/// nothing about origin.
///
/// EMBEDDED signatures only (WTD_CHOICE_FILE). A file signed through a security catalog
/// rather than in its own bytes reads as unsigned here: verified on this machine, where
/// kernel32.dll passes (embedded, CN=Microsoft Windows) while notepad.exe and
/// powershell.exe read unsigned because Windows catalog-signs them. That is the right
/// behavior for our use: SignTool embeds, and a setup downloaded from the internet has no
/// business being in a local catalog. Do not reuse this to vet OS components.</summary>
public static class Authenticode
{
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_WHOLECHAIN = 1;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    /// <summary>True only if <paramref name="path"/> carries a valid Authenticode signature
    /// that chains to a trusted root and is not revoked. False for unsigned, tampered,
    /// self-signed, expired-without-timestamp, or revoked files. Never throws: any failure
    /// to answer the question is a "no".</summary>
    public static bool IsTrusted(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            var file = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = path,
            };
            IntPtr pFile = Marshal.AllocHGlobal((int)file.cbStruct);
            try
            {
                Marshal.StructureToPtr(file, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = pFile,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
                };
                Guid action = WinTrustActionGenericVerifyV2;
                int rc = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return rc == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(pFile);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The subject line of the signing certificate ("CN=..., O=..."), or null if the
    /// file carries no signature at all. Says nothing about whether that signature is VALID,
    /// so never use it without <see cref="IsTrusted"/>.</summary>
    public static string? SignerSubject(string path)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return cert.Subject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The whole check: signed, valid, chained to a trusted root, AND signed by
    /// <paramref name="expectedSubject"/> (a substring match on the certificate subject, e.g.
    /// "CN=Azaan Murtaza"). An empty expected subject means the caller has no certificate to
    /// pin yet, and returns false rather than silently passing: a check that accepts anything
    /// is worse than no check, because it reads as protection that isn't there.</summary>
    public static bool IsSignedBy(string path, string expectedSubject)
    {
        if (string.IsNullOrWhiteSpace(expectedSubject))
            return false;
        if (!IsTrusted(path))
            return false;
        string? subject = SignerSubject(path);
        return subject is not null
               && subject.Contains(expectedSubject, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
