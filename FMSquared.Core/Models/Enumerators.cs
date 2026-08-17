namespace FMSquared.Core.Models;

/// <summary>
/// Tracks whether a game item needs to be written to the SD card.
/// </summary>
public enum WorkMode
{
    /// <summary>Item is already on the SD card and unchanged.</summary>
    None,
    /// <summary>Item is new and needs to be copied to the SD card.</summary>
    New,
    /// <summary>Item is on the SD card but needs to be moved/renumbered.</summary>
    Move
}

/// <summary>
/// The disc image format of a game.
/// </summary>
public enum FileFormat
{
    /// <summary>Standard uncompressed disc image (CDI, MDF, IMG, BIN, ISO).</summary>
    Uncompressed,
    /// <summary>CloneCD format (CCD + IMG + SUB).</summary>
    CloneCd,
    /// <summary>CUE/BIN format (needs conversion to CCD/IMG/SUB).</summary>
    CueBin,
    /// <summary>CHD format (needs CHD-to-CUE/BIN, then CUE/BIN-to-CCD/IMG/SUB).</summary>
    Chd,
    /// <summary>Compressed archive (.7z, .rar, .zip) containing disc image(s).</summary>
    Compressed
}

/// <summary>
/// The FM Towns ODE the SD card is set up for.
/// </summary>
public enum OdeKind
{
    /// <summary>Could not determine the ODE type.</summary>
    Unknown,
    /// <summary>DocBrown. Menu ISO is ALMANAC.ISO (Almanac).</summary>
    DocBrown,
    /// <summary>Wizard. Menu ISO is SPLLBOOK.ISO (Spellbook).</summary>
    Wizard
}

/// <summary>
/// How to determine the display name of a game.
/// </summary>
public enum RenameBy
{
    /// <summary>Use the folder name.</summary>
    Folder,
    /// <summary>Use the disc image file name.</summary>
    File
}
