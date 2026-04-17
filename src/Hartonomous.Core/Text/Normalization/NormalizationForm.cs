namespace Hartonomous.Core.Text.Normalization;

/// <summary>
/// Unicode Normalization Forms per UAX #15. NFC / NFD are canonical;
/// NFKC / NFKD include compatibility decompositions (fullwidth → ASCII,
/// superscripts → digits, ligatures → constituent letters, etc.).
/// </summary>
public enum NormalizationForm : byte
{
    Nfc = 0,
    Nfd,
    Nfkc,
    Nfkd,
}
