using System.ComponentModel;
using System.Runtime.CompilerServices;
using FMSquared.Core.Services;

namespace FMSquared.Core.Models;

/// <summary>
/// Represents a single FM Towns disc image (or placeholder entry) on the SD card.
/// </summary>
public class TownsGame : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private int _sdNumber;
    private WorkMode _workMode = WorkMode.None;
    private bool _isMatch;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Display name of the game (stored in Title.txt on the SD card).
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            var sanitized = AsciiSanitizer.SanitizeName(value);
            if (_name != sanitized) { _name = sanitized; OnPropertyChanged(); }
        }
    }

    /// <summary>
    /// Current numbered folder on the SD card (e.g., 2 for folder "02").
    /// 0 means the item has no numbered folder yet (new from PC, or an
    /// unnumbered folder dropped onto the card root).
    /// </summary>
    public int SdNumber
    {
        get => _sdNumber;
        set { if (_sdNumber != value) { _sdNumber = value; OnPropertyChanged(); OnPropertyChanged(nameof(Location)); OnPropertyChanged(nameof(IsNotOnSdCard)); } }
    }

    /// <summary>
    /// Display string for Location column.
    /// </summary>
    public string Location => !string.IsNullOrEmpty(FullFolderPath) ? "SD card" : "Other";

    /// <summary>
    /// Whether this item needs to be written/moved on the SD card.
    /// </summary>
    public WorkMode WorkMode
    {
        get => _workMode;
        set { if (_workMode != value) { _workMode = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// The disc image format.
    /// </summary>
    public FileFormat FileFormat { get; set; } = FileFormat.Uncompressed;

    /// <summary>
    /// For compressed archives, stores the format of the disc image inside
    /// the archive (e.g., CueBin, CloneCd). Null when FileFormat is not Compressed.
    /// </summary>
    public FileFormat? InnerFileFormat { get; set; }

    /// <summary>
    /// For compressed archives, identifies the disc image entry inside the
    /// archive so save can extract just its file set. Null when FileFormat
    /// is not Compressed.
    /// </summary>
    public ArchiveEntryInfo? SelectedArchiveEntry { get; set; }

    /// <summary>
    /// Full paths to all disc image files (e.g., .ccd + .img + .sub).
    /// </summary>
    public List<string> ImageFiles { get; set; } = new();

    /// <summary>
    /// Full path to the game's folder on the SD card (e.g., "H:\02").
    /// Empty if the item is not on the SD card.
    /// </summary>
    public string FullFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Source path for items being added from PC (not yet on SD card).
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    private long _length;

    /// <summary>
    /// Total size of all disc image files in bytes. -1 hides the size column value.
    /// </summary>
    public long Length
    {
        get => _length;
        set { if (_length != value) { _length = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Whether this item is the menu system (folder 01) and should be locked.
    /// </summary>
    public bool IsMenuItem => SdNumber == Constants.MenuFolderNumber;

    /// <summary>
    /// Whether this entry has a Title.txt but no disc image. Placeholder rows
    /// (e.g., "---Boot From Floppy---") are valid menu entries on DocBrown/Wizard.
    /// </summary>
    public bool IsPlaceholder { get; set; }

    /// <summary>
    /// Whether the info button should be available for this item.
    /// </summary>
    public bool IsGameEntry => !IsMenuItem;

    /// <summary>
    /// Whether this item has not yet been saved to the SD card (added from PC).
    /// </summary>
    public bool IsNotOnSdCard => string.IsNullOrEmpty(FullFolderPath);

    /// <summary>
    /// Set when the user renames the entry so Title.txt gets rewritten on save.
    /// </summary>
    public bool TitleDirty { get; set; }

    /// <summary>
    /// Whether the current search text matches Name.
    /// Transient row highlight state, never saved.
    /// </summary>
    public bool IsMatch
    {
        get => _isMatch;
        set { if (_isMatch != value) { _isMatch = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Generates the formatted folder number string (e.g., "02", "100", "1000").
    /// </summary>
    public string FolderNumberFormatted
    {
        get
        {
            if (SdNumber <= 0) return string.Empty;
            if (SdNumber < 100) return SdNumber.ToString("D2");
            return SdNumber.ToString();
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
