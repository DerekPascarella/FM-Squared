using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FMSquared.Core.Services;

namespace FMSquared.Core.Models;

/// <summary>
/// Represents a single FM Towns disc image (or placeholder entry) on the SD card.
/// </summary>
public class TownsGame : INotifyPropertyChanged
{
    internal Action? EnsureCanMutate { get; set; }
    internal Action<Action>? NotifyObservers { get; set; }

    internal string SaveId { get; set; } = Guid.NewGuid().ToString("N");

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
            EnsureCanMutate?.Invoke();
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
        set { EnsureCanMutate?.Invoke(); if (_sdNumber != value) { _sdNumber = value; OnPropertyChanged(); OnPropertyChanged(nameof(Location)); OnPropertyChanged(nameof(IsNotOnSdCard)); } }
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
        set { EnsureCanMutate?.Invoke(); if (_workMode != value) { _workMode = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// The disc image format.
    /// </summary>
    private FileFormat _fileFormat = FileFormat.Uncompressed;
    public FileFormat FileFormat { get => _fileFormat; set { EnsureCanMutate?.Invoke(); _fileFormat = value; } }

    /// <summary>
    /// For compressed archives, stores the format of the disc image inside
    /// the archive (e.g., CueBin, CloneCd). Null when FileFormat is not Compressed.
    /// </summary>
    private FileFormat? _innerFileFormat = null;
    public FileFormat? InnerFileFormat { get => _innerFileFormat; set { EnsureCanMutate?.Invoke(); _innerFileFormat = value; } }

    /// <summary>
    /// For compressed archives, identifies the disc image entry inside the
    /// archive so save can extract just its file set. Null when FileFormat
    /// is not Compressed.
    /// </summary>
    private ArchiveEntryInfo? _selectedArchiveEntry = null;
    public ArchiveEntryInfo? SelectedArchiveEntry { get => _selectedArchiveEntry; set { EnsureCanMutate?.Invoke(); _selectedArchiveEntry = value; } }

    /// <summary>
    /// Full paths to all disc image files (e.g., .ccd + .img + .sub).
    /// </summary>
    private Collection<string>? _imageFiles;
    public Collection<string> ImageFiles
    {
        get => _imageFiles ??= new GuardedImageCollection(() => EnsureCanMutate?.Invoke());
        set
        {
            EnsureCanMutate?.Invoke();
            _imageFiles = new GuardedImageCollection(() => EnsureCanMutate?.Invoke(), value);
        }
    }

    /// <summary>
    /// Full path to the game's folder on the SD card (e.g., "H:\02").
    /// Empty if the item is not on the SD card.
    /// </summary>
    private string _fullFolderPath = string.Empty;
    public string FullFolderPath { get => _fullFolderPath; set { EnsureCanMutate?.Invoke(); _fullFolderPath = value; } }

    /// <summary>
    /// Source path for items being added from PC (not yet on SD card).
    /// </summary>
    private string _sourcePath = string.Empty;
    public string SourcePath { get => _sourcePath; set { EnsureCanMutate?.Invoke(); _sourcePath = value; } }

    private long _length;

    /// <summary>
    /// Total size of all disc image files in bytes. -1 hides the size column value.
    /// </summary>
    public long Length
    {
        get => _length;
        set { EnsureCanMutate?.Invoke(); if (_length != value) { _length = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Whether this item is the menu system (folder 01) and should be locked.
    /// </summary>
    public bool IsMenuItem => SdNumber == Constants.MenuFolderNumber;

    /// <summary>
    /// Whether this entry has a Title.txt but no disc image. Placeholder rows
    /// (e.g., "---Boot From Floppy---") are valid menu entries on DocBrown/Wizard.
    /// </summary>
    private bool _isPlaceholder = false;
    public bool IsPlaceholder { get => _isPlaceholder; set { EnsureCanMutate?.Invoke(); _isPlaceholder = value; } }

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
    private bool _titleDirty = false;
    public bool TitleDirty { get => _titleDirty; set { EnsureCanMutate?.Invoke(); _titleDirty = value; } }

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
        void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (NotifyObservers == null) Notify();
        else NotifyObservers(Notify);
    }
}
