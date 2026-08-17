using FMSquared.Core.Models;

namespace FMSquared.Core.Services;

/// <summary>
/// Determines whether an SD card is set up for the DocBrown or Wizard ODE.
/// </summary>
public static class OdeDetector
{
    /// <summary>
    /// Detects the ODE type by looking at the menu ISO / menu data in folder 01,
    /// then falling back to the ODE settings file in the card root.
    /// </summary>
    public static OdeKind Detect(string sdCardPath)
    {
        string menuFolder = Path.Combine(sdCardPath, Constants.MenuFolderName);

        // Strongest signal: which menu ISO (or menu executable) is present.
        if (File.Exists(Path.Combine(menuFolder, Constants.DocBrownMenuIsoName)) ||
            File.Exists(Path.Combine(menuFolder, Constants.MenuDataFolderName, "ALMANAC.EXP")))
            return OdeKind.DocBrown;

        if (File.Exists(Path.Combine(menuFolder, Constants.WizardMenuIsoName)) ||
            File.Exists(Path.Combine(menuFolder, Constants.MenuDataFolderName, "SPLLBOOK.EXP")))
            return OdeKind.Wizard;

        // Fall back to the settings file the ODE firmware reads from the root.
        if (File.Exists(Path.Combine(sdCardPath, Constants.DocBrownIniFile)))
            return OdeKind.DocBrown;

        if (File.Exists(Path.Combine(sdCardPath, Constants.WizardIniFile)))
            return OdeKind.Wizard;

        return OdeKind.Unknown;
    }
}
