// AUTO-GENERATED FILE. Version is read from ../version.txt during build.
// Do not edit by hand. Update ../version.txt for the version, or
// UpdateVersion.ps1 to change anything else in this file.

namespace FMSquared.Core;

public static class Constants
{
    public const string Version = "2.0.0";
    public const string AppName = "FM^2";
    public const string AppDescription = "An SD card management tool for the FM Towns/FM Towns Marty ODEs DocBrown and Wizard.";
    public const string AppUrl = "https://github.com/DerekPascarella/FM-Squared";

    // Sidecar text file names
    public const string TitleFile = "Title.txt";
    public const string GameListFile = "GameList.txt";

    // ODE settings files (SD card root)
    public const string DocBrownIniFile = "DocBrown.ini";
    public const string WizardIniFile = "Wizard.ini";

    // Menu build inputs/outputs inside folder 01
    public const string MenuDataFolderName = "data";
    public const string MenuBootFileName = "boot_cd";
    public const string TitlesFileName = "TITLES.TXT";
    public const string MenuIoSysName = "IO.SYS";
    public const string MenuEmptyBinName = "EMPTY.BIN";
    public const string DocBrownMenuIsoName = "ALMANAC.ISO";
    public const string WizardMenuIsoName = "SPLLBOOK.ISO";
    public const string DocBrownVolumeId = "Almanac";
    public const string WizardVolumeId = "SpellBook";
    public const string IsoSystemId = "Win32";

    // Supported disc image extensions
    public static readonly string[] DiscImageExtensions = { ".cdi", ".mdf", ".img", ".bin", ".iso" };
    public static readonly string[] CloneCdExtensions = { ".ccd" };
    public static readonly string[] CueBinExtensions = { ".cue" };
    public static readonly string[] ChdExtensions = { ".chd" };
    public static readonly string[] AllImageExtensions = { ".cdi", ".mdf", ".img", ".bin", ".iso", ".ccd", ".cue", ".mds" };
    public static readonly string[] ArchiveExtensions = { ".7z", ".rar", ".zip" };

    // Folder numbering
    public const int MenuFolderNumber = 1;
    public const string MenuFolderName = "01";
    public const string TempFolderName = "fmsquared_temp";
    public const string InvalidFolderPrefix = "INVALID_";

    // Folders on the SD card that are never treated as game folders.
    public static readonly string[] IgnoredFolderNames = {
        "System Volume Information", TempFolderName, "towns_sorter_temp"
    };
}