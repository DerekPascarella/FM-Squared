using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using ByteSizeLib;
using FMSquared.Core.Models;
using FMSquared.Core.Services;

namespace FMSquared.Core;

/// <summary>
/// Central orchestrator for all SD card operations.
/// Manages the game list, scanning, sorting, saving, and menu ISO rebuilding.
///
/// The DocBrown/Wizard ODEs list games by FAT directory order, so saving
/// moves every game folder into a temporary folder on the card and then
/// moves each one back in list order as numbered folders 02 through N.
/// This recreates the root directory entries in the exact order the ODE
/// menu expects.
/// </summary>
public class Manager : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _sdCardPath = string.Empty;
    private OdeKind _odeKindDetected = OdeKind.Unknown;
    private OdeKind _odeKindSelected = OdeKind.Unknown;

    /// <summary>
    /// The list of all games currently loaded, including the menu item (folder 01).
    /// </summary>
    public ObservableCollection<TownsGame> ItemList { get; } = new();

    /// <summary>
    /// Manages undo/redo operations.
    /// </summary>
    public UndoManager UndoManager { get; } = new();

    // On-card items removed from the list. Their folders are deleted on save.
    private readonly List<TownsGame> _removedItems = new();

    /// <summary>
    /// Path to the SD card root (e.g., "H:\").
    /// </summary>
    public string SdCardPath
    {
        get => _sdCardPath;
        set { _sdCardPath = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The ODE type detected on the current SD card.
    /// </summary>
    public OdeKind OdeKindDetected
    {
        get => _odeKindDetected;
        set { _odeKindDetected = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The menu type the user has selected (Almanac for DocBrown,
    /// Spellbook for Wizard). Defaults to the detected type after loading.
    /// </summary>
    public OdeKind OdeKindSelected
    {
        get => _odeKindSelected;
        set { _odeKindSelected = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Path to the application's tools directory, which holds the bundled
    /// Almanac and Spellbook menu file sets.
    /// </summary>
    public string ToolsPath { get; set; } = string.Empty;

    /// <summary>
    /// Whether to run lock checks before save operations.
    /// </summary>
    public bool EnableLockCheck { get; set; } = true;

    /// <summary>
    /// Callback for when a folder move fails due to a file lock. The UI sets this
    /// to prompt the user. Return true to retry, false to abort.
    /// </summary>
    public Func<string, Task<bool>>? OnFolderLocked { get; set; }

    /// <summary>
    /// Callback for non-fatal archive warnings (e.g., an archive holding
    /// more than one disc image). The UI sets this to show a message box.
    /// </summary>
    public Func<string, Task>? OnArchiveWarning { get; set; }

    /// <summary>
    /// Whether the menu ISO can be rebuilt, either from menu files already in
    /// folder 01 or from the bundled menu assets in the tools directory.
    /// </summary>
    public bool CanBuildMenu =>
        !string.IsNullOrEmpty(SdCardPath) &&
        (MenuIsoBuilder.CanBuild(SdCardPath) || HasBundledMenuAssets(OdeKindSelected));

    /// <summary>
    /// Whether the tools directory holds a complete menu file set for the
    /// given menu type.
    /// </summary>
    public bool HasBundledMenuAssets(OdeKind odeKind)
    {
        string assetDir = GetMenuAssetPath(odeKind);
        if (string.IsNullOrEmpty(assetDir)) return false;

        return File.Exists(Path.Combine(assetDir, Constants.MenuBootFileName)) &&
               File.Exists(Path.Combine(assetDir, Constants.MenuDataFolderName, Constants.MenuIoSysName));
    }

    private string GetMenuAssetPath(OdeKind odeKind)
    {
        if (string.IsNullOrEmpty(ToolsPath)) return string.Empty;
        return Path.Combine(ToolsPath, odeKind == OdeKind.Wizard ? "spellbook" : "almanac");
    }

    /// <summary>
    /// Scans the SD card and populates ItemList.
    /// </summary>
    public async Task LoadItemsFromCardAsync()
    {
        if (string.IsNullOrEmpty(SdCardPath) || !Directory.Exists(SdCardPath))
            throw new InvalidOperationException("Invalid SD card path.");

        ItemList.Clear();
        _removedItems.Clear();
        UndoManager.Clear();

        OdeKindDetected = await Task.Run(() => OdeDetector.Detect(SdCardPath));
        OdeKindSelected = OdeKindDetected;

        // Insert a synthetic menu entry for folder 01.
        string menuPath = Path.Combine(SdCardPath, Constants.MenuFolderName);
        var menuItem = new TownsGame
        {
            Name = "MENU",
            SdNumber = Constants.MenuFolderNumber,
            FullFolderPath = menuPath,
            WorkMode = WorkMode.None,
            Length = -1
        };
        ItemList.Add(menuItem);

        await CardScanner.ScanCardAsync(SdCardPath, game => ItemList.Add(game));

        await RecoverStagedGamesAsync();
    }

    // Folders stranded in the temporary folder by an interrupted save come back
    // as unnumbered items. The next save moves them into numbered folders.
    private async Task RecoverStagedGamesAsync()
    {
        string tempDir = Path.Combine(SdCardPath, Constants.TempFolderName);
        if (!Directory.Exists(tempDir))
            return;

        await CardScanner.ScanCardAsync(tempDir, game =>
        {
            game.SdNumber = 0;
            ItemList.Add(game);
        });
    }

    /// <summary>
    /// Sorts the item list alphabetically by title (menu item stays first),
    /// recording the change for undo.
    /// </summary>
    public void SortList()
    {
        var oldOrder = ItemList.ToList();
        SortListInternal();

        UndoManager.RecordChange(new ListReorderOperation("Sort List")
        {
            ItemList = ItemList,
            OldOrder = oldOrder,
            NewOrder = ItemList.ToList()
        });
    }

    private void SortListInternal()
    {
        var menuEntry = ItemList.FirstOrDefault(g => g.IsMenuItem);
        var sorted = ItemList
            .Where(g => !g.IsMenuItem)
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ItemList.Clear();
        if (menuEntry != null)
            ItemList.Add(menuEntry);
        foreach (var game in sorted)
            ItemList.Add(game);
    }

    /// <summary>
    /// Saves all changes to the SD card: renumbers folders to match the list
    /// order, rewrites FAT directory order via the temp-folder shuffle, copies
    /// new items, writes Title.txt/GameList.txt, and rebuilds the menu ISO.
    /// </summary>
    public async Task SaveAsync(IProgress<string>? progress = null, IProgress<int>? itemProgress = null, string? tempFolderRoot = null)
    {
        if (string.IsNullOrEmpty(SdCardPath))
            throw new InvalidOperationException("No SD card path set.");

        // Fail before the card is touched when an archive is missing or no
        // longer matches what was captured at add time. Data corruption
        // deeper in an archive still surfaces during extraction.
        foreach (var game in ItemList)
        {
            if (game.WorkMode != WorkMode.New || game.FileFormat != FileFormat.Compressed)
                continue;
            if (game.SelectedArchiveEntry == null)
                continue;

            progress?.Report($"Checking {Path.GetFileName(game.SourcePath)}...");
            var entries = await Task.Run(() => ArchiveHelper.GetArchiveEntries(game.SourcePath));
            var current = entries.ElementAtOrDefault(game.SelectedArchiveEntry.Ordinal);
            if (current == null ||
                current.Size != game.SelectedArchiveEntry.Size ||
                !ArchiveEntryPath.HasSameIdentityKey(current.FullName, game.SelectedArchiveEntry.FullName))
                throw new InvalidOperationException(
                    $"The archive \"{Path.GetFileName(game.SourcePath)}\" changed since it was added. " +
                    "Remove the entry and add the archive again.");
        }

        // Install the bundled menu files first on a fresh card so folder 01
        // gets the first FAT directory entry, ahead of the game folders.
        if (CanBuildMenu)
            await EnsureMenuFilesAsync(progress);

        // Assign final folder numbers (menu = 01, games = 02..N in list order).
        int folderNum = 1;
        foreach (var game in ItemList)
        {
            if (game.IsMenuItem) continue;
            folderNum++;

            if (game.WorkMode != WorkMode.New)
                game.WorkMode = WorkMode.Move;

            game.SdNumber = folderNum;
        }

        // Delete folders for items the user removed from the list.
        foreach (var removed in _removedItems)
        {
            if (string.IsNullOrEmpty(removed.FullFolderPath) || !Directory.Exists(removed.FullFolderPath))
                continue;

            progress?.Report($"Deleting {removed.Name}...");
            await Task.Run(() => Directory.Delete(removed.FullFolderPath, recursive: true));
        }
        _removedItems.Clear();

        // Delete orphaned numbered folders not claimed by any item.
        await DeleteOrphanedFoldersAsync(progress);

        // FAT-order shuffle phase 1: move every on-card game folder into
        // the temporary folder.
        string tempDir = Path.Combine(SdCardPath, Constants.TempFolderName);
        Directory.CreateDirectory(tempDir);

        var onCardItems = ItemList
            .Where(g => !g.IsMenuItem && g.WorkMode != WorkMode.New &&
                        !string.IsNullOrEmpty(g.FullFolderPath) && Directory.Exists(g.FullFolderPath))
            .ToList();

        progress?.Report("Staging game folders for FAT sorting...");

        foreach (var game in onCardItems)
        {
            string stagedPath = Path.Combine(tempDir, Path.GetFileName(game.FullFolderPath));

            // Already in the temp folder, left there by an interrupted save.
            if (string.Equals(game.FullFolderPath, stagedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (Directory.Exists(stagedPath))
                stagedPath = Path.Combine(tempDir,
                    Path.GetFileName(game.FullFolderPath) + "_" + FolderHelper.GenerateUniqueTag());

            await FolderHelper.MoveDirectoryAsync(game.FullFolderPath, stagedPath, OnFolderLocked);
            game.FullFolderPath = stagedPath;
        }

        // Phase 2: recreate the numbered folders in final order so the FAT
        // directory entries land in alphabetical title order.
        int processed = 0;

        foreach (var game in ItemList)
        {
            if (game.IsMenuItem) continue;

            processed++;
            itemProgress?.Report(processed);

            string destFolder = Path.Combine(SdCardPath, game.FolderNumberFormatted);

            if (game.WorkMode == WorkMode.New)
            {
                await CopyNewItemAsync(game, destFolder, progress, tempFolderRoot);
            }
            else
            {
                progress?.Report($"Folder {game.FolderNumberFormatted}: {game.Name}");
                await FolderHelper.MoveDirectoryAsync(game.FullFolderPath, destFolder, OnFolderLocked);
            }

            game.FullFolderPath = destFolder;
            game.WorkMode = WorkMode.None;

            // Write Title.txt when missing or renamed.
            string titlePath = Path.Combine(destFolder, Constants.TitleFile);
            if (game.TitleDirty || !File.Exists(titlePath))
            {
                await Task.Run(() => File.WriteAllText(titlePath, game.Name, new UTF8Encoding(false)));
                game.TitleDirty = false;
            }
        }

        // Delete the temp folder only when it is empty. Anything still inside
        // came from an interrupted save and gets recovered on the next load.
        try { Directory.Delete(tempDir, recursive: false); } catch { }

        // Write GameList.txt to the card root for the user's convenience.
        progress?.Report("Writing GameList.txt...");
        string gameList = GenerateGameList();
        await Task.Run(() =>
            File.WriteAllText(Path.Combine(SdCardPath, Constants.GameListFile), gameList, new UTF8Encoding(false)));

        // Rebuild the Almanac/Spellbook menu ISO.
        if (CanBuildMenu)
        {
            EnsureOdeIniFile();

            string menuName = OdeKindSelected == OdeKind.Wizard ? "Spellbook" : "Almanac";
            progress?.Report($"Rebuilding {menuName} menu ISO...");
            await Task.Run(() => MenuIsoBuilder.Build(SdCardPath, OdeKindSelected, ItemList));
        }
        else
        {
            progress?.Report("Menu ISO was not rebuilt because the menu files are missing.");
        }

        progress?.Report("Done!");
    }

    /// <summary>
    /// Generates the GameList.txt content ("01 - MENU" plus one line per game).
    /// </summary>
    public string GenerateGameList()
    {
        var sb = new StringBuilder();
        sb.Append("01 - MENU\r\n");

        foreach (var game in ItemList)
        {
            if (game.IsMenuItem) continue;
            sb.Append(game.FolderNumberFormatted).Append(" - ").Append(game.Name).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Adds game(s) from file paths (disc images, archives, or folders containing them).
    /// New items are staged with SdNumber = 0 and WorkMode = New.
    /// </summary>
    public async Task<List<TownsGame>> AddGamesAsync(string[] paths, IProgress<string>? progress = null, int insertIndex = -1)
    {
        var added = new List<TownsGame>();
        var archiveWarnings = new List<string>();

        foreach (var path in paths)
        {
            progress?.Report($"Adding {Path.GetFileName(path)}...");

            TownsGame? game = null;

            if (Directory.Exists(path))
            {
                game = await Task.Run(() => LoadGameFromSource(path, specificFile: null));
            }
            else if (File.Exists(path) && ArchiveHelper.IsArchive(path))
            {
                game = await Task.Run(() => LoadGameFromArchive(path, archiveWarnings));
            }
            else if (File.Exists(path))
            {
                string parentDir = Path.GetDirectoryName(path)!;
                game = await Task.Run(() => LoadGameFromSource(parentDir, specificFile: path));
            }

            if (game != null)
            {
                game.SdNumber = 0;
                game.WorkMode = WorkMode.New;
                game.TitleDirty = true;

                if (insertIndex >= 0 && insertIndex <= ItemList.Count)
                {
                    ItemList.Insert(insertIndex, game);
                    insertIndex++;
                }
                else
                {
                    ItemList.Add(game);
                }

                added.Add(game);
            }
        }

        if (added.Count > 0)
        {
            var undoOp = new MultiItemAddOperation { ItemList = ItemList };
            foreach (var game in added)
                undoOp.Items.Add((game, ItemList.IndexOf(game)));
            UndoManager.RecordChange(undoOp);
        }

        if (OnArchiveWarning != null)
        {
            foreach (var warning in archiveWarnings)
                await OnArchiveWarning(warning);
        }

        return added;
    }

    /// <summary>
    /// Title used for the floppy boot placeholder entry. Its leading dashes
    /// sort before letters, keeping it at the top of the game list.
    /// </summary>
    public const string FloppyBootTitle = "---Boot From Floppy---";

    /// <summary>
    /// Whether the list already holds a floppy boot placeholder entry.
    /// </summary>
    public bool HasFloppyBootEntry =>
        ItemList.Any(g => g.IsPlaceholder &&
            string.Equals(g.Name, FloppyBootTitle, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Inserts a floppy boot placeholder entry right below the menu item.
    /// The entry is an empty folder with only a Title.txt, which makes the
    /// FM Towns prompt for bootable media when selected.
    /// </summary>
    public TownsGame InsertFloppyBootEntry()
    {
        var game = new TownsGame
        {
            Name = FloppyBootTitle,
            IsPlaceholder = true,
            WorkMode = WorkMode.New,
            TitleDirty = true,
            SdNumber = 0,
            Length = -1
        };

        int insertIndex = ItemList.Any(g => g.IsMenuItem) ? 1 : 0;
        ItemList.Insert(insertIndex, game);

        var undoOp = new MultiItemAddOperation { ItemList = ItemList };
        undoOp.Items.Add((game, insertIndex));
        UndoManager.RecordChange(undoOp);

        return game;
    }

    /// <summary>
    /// Removes selected items from the list. Items already on the SD card
    /// will have their folders deleted on save.
    /// </summary>
    public void RemoveItems(IEnumerable<TownsGame> items)
    {
        var toRemove = items.Where(i => !i.IsMenuItem).ToList();
        if (toRemove.Count == 0) return;

        var undoOp = new MultiItemRemoveOperation
        {
            ItemList = ItemList,
            OnItemRestored = item => _removedItems.Remove(item),
            OnItemRemoved = item =>
            {
                if (!string.IsNullOrEmpty(item.FullFolderPath) && !_removedItems.Contains(item))
                    _removedItems.Add(item);
            }
        };
        foreach (var item in toRemove)
            undoOp.Items.Add((item, ItemList.IndexOf(item)));

        foreach (var item in toRemove)
        {
            ItemList.Remove(item);

            if (!string.IsNullOrEmpty(item.FullFolderPath))
                _removedItems.Add(item);
        }

        UndoManager.RecordChange(undoOp);
    }

    /// <summary>
    /// Counts numbered game folders (02+) on the SD card.
    /// </summary>
    public int CountGameFolders()
    {
        if (string.IsNullOrEmpty(SdCardPath) || !Directory.Exists(SdCardPath))
            return 0;

        return Directory.GetDirectories(SdCardPath)
            .Count(d =>
            {
                string name = Path.GetFileName(d);
                return int.TryParse(name, out _) && name != Constants.MenuFolderName;
            });
    }

    /// <summary>
    /// Collects all folder paths that will be read/written/moved during save.
    /// Used by the lock checker to verify accessibility before starting.
    /// </summary>
    public List<string> CollectPathsToModify()
    {
        var paths = new List<string>();

        // Menu folder (01) gets GameList.txt and the rebuilt ISO.
        string menuPath = Path.Combine(SdCardPath, Constants.MenuFolderName);
        if (Directory.Exists(menuPath))
            paths.Add(menuPath);

        // All existing game folders
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in ItemList.Where(g => !g.IsMenuItem && !string.IsNullOrEmpty(g.FullFolderPath)))
        {
            if (Directory.Exists(game.FullFolderPath))
            {
                paths.Add(game.FullFolderPath);
                knownPaths.Add(game.FullFolderPath);
            }
        }

        // Folders pending deletion
        foreach (var removed in _removedItems)
        {
            if (Directory.Exists(removed.FullFolderPath) && knownPaths.Add(removed.FullFolderPath))
                paths.Add(removed.FullFolderPath);
        }

        // Orphan numbered folders also get touched.
        foreach (var dir in Directory.GetDirectories(SdCardPath))
        {
            string folderName = Path.GetFileName(dir);
            if (int.TryParse(folderName, out int num) && num != Constants.MenuFolderNumber && !knownPaths.Contains(dir))
                paths.Add(dir);
        }

        return paths;
    }

    /// <summary>
    /// Estimates whether the SD card has enough free space for the pending
    /// save. Sizes for compressed and converted images are estimates.
    /// </summary>
    public async Task<SpaceCheckResult> CalculateRequiredSpaceAsync()
    {
        var result = new SpaceCheckResult
        {
            MetadataBuffer = 1 * 1024 * 1024
        };

        if (string.IsNullOrEmpty(SdCardPath) || !Directory.Exists(SdCardPath))
        {
            // Nothing to measure against, let the save proceed.
            result.HasSufficientSpace = true;
            return result;
        }

        await Task.Run(() =>
        {
            result.AvailableSpace = GetAvailableSpace(SdCardPath);

            // Menu footprint. Rebuilding in place only needs headroom for the
            // new ISO. A fresh install also needs the asset files plus the
            // ISO built from them.
            result.MenuFolderExists = Directory.Exists(Path.Combine(SdCardPath, Constants.MenuFolderName));

            const long menuWiggleRoom = 5L * 1024 * 1024;
            long menuAssetSize = 0;
            if (!MenuIsoBuilder.CanBuild(SdCardPath) && HasBundledMenuAssets(OdeKindSelected))
                menuAssetSize = GetDirectorySize(GetMenuAssetPath(OdeKindSelected));
            result.MenuSpaceNeeded = menuAssetSize * 2 + menuWiggleRoom;

            // Folders deleted during save give their space back before the
            // copies start.
            var countedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var removed in _removedItems)
            {
                if (string.IsNullOrEmpty(removed.FullFolderPath) || !Directory.Exists(removed.FullFolderPath))
                    continue;

                if (countedFolders.Add(Path.GetFileName(removed.FullFolderPath)))
                    result.SpaceToBeFreed += GetDirectorySize(removed.FullFolderPath);
            }

            var knownFolders = new HashSet<string>(
                ItemList.Where(g => !g.IsMenuItem && !string.IsNullOrEmpty(g.FullFolderPath))
                        .Select(g => Path.GetFileName(g.FullFolderPath)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var dir in Directory.GetDirectories(SdCardPath))
            {
                string folderName = Path.GetFileName(dir);
                if (!int.TryParse(folderName, out int num)) continue;
                if (num == Constants.MenuFolderNumber) continue;

                if (!knownFolders.Contains(folderName) && countedFolders.Add(folderName))
                    result.SpaceToBeFreed += GetDirectorySize(dir);
            }

            // Size of the items that will be copied onto the card.
            foreach (var game in ItemList)
            {
                if (game.WorkMode != WorkMode.New || game.IsPlaceholder) continue;

                result.NewItemCount++;
                long size = Math.Max(game.Length, 0);

                var format = game.FileFormat == FileFormat.Compressed
                    ? game.InnerFileFormat ?? FileFormat.Uncompressed
                    : game.FileFormat;

                if (game.FileFormat == FileFormat.Compressed)
                    result.ContainsEstimatedSizes = true;

                if (format == FileFormat.CueBin)
                {
                    // Conversion to CCD/IMG/SUB writes full 2352 byte sectors
                    // plus 96 bytes of subchannel each, so images ripped with
                    // 2048 byte sectors grow by roughly a fifth.
                    size = (long)(size * 1.25);
                    result.ContainsEstimatedSizes = true;
                }
                else if (format == FileFormat.Chd)
                {
                    // Length holds the compressed CHD size, the converted
                    // output will be larger.
                    size *= 2;
                    result.ContainsEstimatedSizes = true;
                }

                result.NewItemsSize += size;
            }

            result.TotalNeeded = result.NewItemsSize + result.MenuSpaceNeeded + result.MetadataBuffer;
            result.EffectiveAvailable = result.AvailableSpace + result.SpaceToBeFreed;
            result.Shortfall = result.TotalNeeded - result.EffectiveAvailable;
            result.HasSufficientSpace = result.Shortfall <= 0;
        });

        return result;
    }

    /// <summary>
    /// Builds the warning text shown when the space check comes up short.
    /// </summary>
    public static string BuildSpaceWarningMessage(SpaceCheckResult spaceCheck)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Insufficient space on the SD card.\n");
        sb.AppendLine("Space needed:");
        sb.AppendLine($"  • New disc images ({spaceCheck.NewItemCount}): {FormatBytes(spaceCheck.NewItemsSize)}");
        sb.AppendLine($"  • Menu files: ~{FormatBytes(spaceCheck.MenuSpaceNeeded)}");
        sb.AppendLine($"  • Metadata files: ~{FormatBytes(spaceCheck.MetadataBuffer)}");
        sb.AppendLine($"  Total: ~{FormatBytes(spaceCheck.TotalNeeded)}\n");
        sb.AppendLine($"Space available: {FormatBytes(spaceCheck.AvailableSpace)}");
        if (spaceCheck.SpaceToBeFreed > 0)
        {
            sb.AppendLine($"Space to be freed: {FormatBytes(spaceCheck.SpaceToBeFreed)}");
            sb.AppendLine($"Effective available: {FormatBytes(spaceCheck.EffectiveAvailable)}");
        }
        sb.AppendLine($"\nShortfall: ~{FormatBytes(spaceCheck.Shortfall)}");
        if (spaceCheck.ContainsEstimatedSizes)
            sb.AppendLine("\nNote: Some items are compressed or need conversion and their final sizes are estimates.");
        sb.Append("\nDo you want to proceed anyway?");
        return sb.ToString();
    }

    private static string FormatBytes(long bytes) => ByteSize.FromBytes(bytes).ToString("0.##");

    private static long GetAvailableSpace(string path)
    {
        try
        {
            // Windows paths resolve straight from the root. On Linux and
            // macOS the root is always "/", so find the longest mount point
            // that contains the path instead.
            string? pathRoot = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(pathRoot) && pathRoot != "/" && pathRoot != "\\")
                return new DriveInfo(pathRoot).AvailableFreeSpace;

            string fullPath = Path.GetFullPath(path);
            if (!fullPath.EndsWith(Path.DirectorySeparatorChar))
                fullPath += Path.DirectorySeparatorChar;

            DriveInfo? best = null;
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;

                string mountPath = drive.RootDirectory.FullName;
                if (!mountPath.EndsWith(Path.DirectorySeparatorChar))
                    mountPath += Path.DirectorySeparatorChar;

                if (fullPath.StartsWith(mountPath, StringComparison.Ordinal) &&
                    (best == null || mountPath.Length > best.RootDirectory.FullName.Length))
                {
                    best = drive;
                }
            }

            return best?.AvailableFreeSpace ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }

    public bool SearchInItem(TownsGame item, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return false;

        return item.Name?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // --- Private helpers ---

    // When the card holds the other ODE's menu, that menu is removed first and the
    // new set is installed in full, so a switch leaves no trace of the old one.
    private async Task EnsureMenuFilesAsync(IProgress<string>? progress)
    {
        // Check what is on the card right now instead of trusting the
        // detection cached at load time.
        OdeKind onCard = OdeDetector.Detect(SdCardPath);
        bool switching = onCard != OdeKind.Unknown &&
                         OdeKindSelected != OdeKind.Unknown &&
                         onCard != OdeKindSelected;

        if (!switching && MenuIsoBuilder.CanBuild(SdCardPath))
            return;

        string menuName = OdeKindSelected == OdeKind.Wizard ? "Spellbook" : "Almanac";

        if (!HasBundledMenuAssets(OdeKindSelected))
        {
            if (switching)
                throw new InvalidOperationException(
                    $"Cannot switch to the {menuName} menu because the bundled " +
                    "menu files are missing from the tools folder.");
            return;
        }

        string assetDir = GetMenuAssetPath(OdeKindSelected);
        string menuFolder = Path.Combine(SdCardPath, Constants.MenuFolderName);

        if (switching)
        {
            string oldName = onCard == OdeKind.Wizard ? "Spellbook" : "Almanac";
            progress?.Report($"Removing {oldName} menu files...");
            await Task.Run(() => RemoveMenuFiles(onCard));
        }

        progress?.Report($"Installing {menuName} menu files into folder 01...");

        await Task.Run(() =>
        {
            foreach (var sourceFile in Directory.EnumerateFiles(assetDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(assetDir, sourceFile);
                string destFile = Path.Combine(menuFolder, relativePath);

                if (!switching && File.Exists(destFile))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                File.Copy(sourceFile, destFile, overwrite: true);
            }
        });
    }

    // Covers the menu ISO and build inputs in folder 01, plus the settings INI in the
    // card root. Anything else the user keeps in folder 01 is left alone.
    private void RemoveMenuFiles(OdeKind odeKind)
    {
        string menuFolder = Path.Combine(SdCardPath, Constants.MenuFolderName);

        string isoName = odeKind == OdeKind.Wizard
            ? Constants.WizardMenuIsoName
            : Constants.DocBrownMenuIsoName;
        string iniName = odeKind == OdeKind.Wizard
            ? Constants.WizardIniFile
            : Constants.DocBrownIniFile;

        if (Directory.Exists(menuFolder))
        {
            File.Delete(Path.Combine(menuFolder, isoName));
            File.Delete(Path.Combine(menuFolder, Constants.MenuBootFileName));

            string dataFolder = Path.Combine(menuFolder, Constants.MenuDataFolderName);
            if (Directory.Exists(dataFolder))
                Directory.Delete(dataFolder, recursive: true);
        }

        File.Delete(Path.Combine(SdCardPath, iniName));
    }

    // An INI already on the card is left as the user configured it.
    private void EnsureOdeIniFile()
    {
        string iniName = OdeKindSelected == OdeKind.Wizard
            ? Constants.WizardIniFile
            : Constants.DocBrownIniFile;
        string iniPath = Path.Combine(SdCardPath, iniName);
        if (File.Exists(iniPath))
            return;

        string defaultIni = Path.Combine(ToolsPath, "defaults", iniName);
        if (File.Exists(defaultIni))
            File.Copy(defaultIni, iniPath);
    }

    private async Task DeleteOrphanedFoldersAsync(IProgress<string>? progress)
    {
        var knownFolders = new HashSet<string>(
            ItemList.Where(g => !g.IsMenuItem && !string.IsNullOrEmpty(g.FullFolderPath))
                    .Select(g => Path.GetFileName(g.FullFolderPath)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var dir in Directory.GetDirectories(SdCardPath))
        {
            string folderName = Path.GetFileName(dir);
            if (!int.TryParse(folderName, out int num)) continue;
            if (num == Constants.MenuFolderNumber) continue;

            if (!knownFolders.Contains(folderName))
            {
                progress?.Report($"Deleting orphaned folder {folderName}...");
                await Task.Run(() => Directory.Delete(dir, recursive: true));
            }
        }
    }

    // Archives are extracted and CUE/BIN is converted to CCD/IMG/SUB along the way.
    private async Task CopyNewItemAsync(TownsGame game, string destFolder, IProgress<string>? progress, string? tempFolderRoot)
    {
        if (game.IsPlaceholder)
        {
            Directory.CreateDirectory(destFolder);
            return;
        }

        if (game.FileFormat == FileFormat.Compressed)
        {
            await UncompressAndCopyAsync(game, destFolder, progress, tempFolderRoot);
        }
        else if (game.FileFormat == FileFormat.Chd)
        {
            await ConvertChdAndCopyAsync(game, destFolder, progress, tempFolderRoot);
        }
        else if (game.FileFormat == FileFormat.CueBin)
        {
            progress?.Report($"Converting {game.Name} (CUE/BIN to CCD/IMG/SUB)...");

            string? cueFile = game.ImageFiles
                .FirstOrDefault(f => Path.GetExtension(f).Equals(".cue", StringComparison.OrdinalIgnoreCase));

            if (cueFile != null)
                await Cue2CcdConverter.ConvertAsync(cueFile, destFolder, progress);
            else
                Directory.CreateDirectory(destFolder);
        }
        else
        {
            progress?.Report($"Copying {game.Name} to folder {game.FolderNumberFormatted}...");

            Directory.CreateDirectory(destFolder);
            foreach (var file in game.ImageFiles)
            {
                if (!File.Exists(file)) continue;
                string destFile = Path.Combine(destFolder, Path.GetFileName(file));
                await Task.Run(() => File.Copy(file, destFile, overwrite: true));
            }
        }

        game.FileFormat = FileFormat.Uncompressed;
        game.InnerFileFormat = null;
        game.SelectedArchiveEntry = null;

        // Re-populate image files from the new location.
        game.ImageFiles.Clear();
        long totalSize = 0;
        foreach (var file in Directory.GetFiles(destFolder))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (Constants.AllImageExtensions.Contains(ext) || ext == ".sub" || ext == ".mdf")
            {
                game.ImageFiles.Add(file);
                totalSize += new FileInfo(file).Length;
            }
        }
        game.Length = totalSize;
    }

    // The disc image format is detected from the extracted contents, not from the
    // archive name, so a CUE/BIN inside a .zip still converts.
    private async Task UncompressAndCopyAsync(TownsGame game, string destFolder, IProgress<string>? progress, string? tempFolderRoot = null)
    {
        string archivePath = game.SourcePath;
        if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
            throw new FileNotFoundException(
                $"Archive file not found: {Path.GetFileName(archivePath)}");

        string tempRoot = !string.IsNullOrEmpty(tempFolderRoot) && Directory.Exists(tempFolderRoot)
            ? tempFolderRoot
            : Path.GetTempPath();
        string tempExtractDir = Path.Combine(tempRoot,
            "FMSquared_ext_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            progress?.Report($"Extracting {Path.GetFileName(archivePath)}...");
            await Task.Run(() =>
            {
                if (game.SelectedArchiveEntry != null)
                    ArchiveHelper.ExtractArchiveForEntry(archivePath, tempExtractDir, game.SelectedArchiveEntry);
                else
                    ArchiveHelper.ExtractArchive(archivePath, tempExtractDir);
            });

            Directory.CreateDirectory(destFolder);

            var extractedFiles = Directory.GetFiles(tempExtractDir);

            string? chdFile = extractedFiles
                .FirstOrDefault(f => Path.GetExtension(f).Equals(".chd", StringComparison.OrdinalIgnoreCase));

            string? cueFile = extractedFiles
                .FirstOrDefault(f => Path.GetExtension(f).Equals(".cue", StringComparison.OrdinalIgnoreCase));

            if (chdFile != null)
            {
                string tempCueDir = Path.Combine(tempExtractDir, "cue_temp");
                var (success, message, cuePath) = await ChdConverter.ConvertToCueBinAsync(
                    chdFile, tempCueDir, progress, gameName: game.Name);

                if (!success || cuePath == null)
                    throw new InvalidOperationException(
                        $"CHD conversion failed for {game.Name}: {message}");

                progress?.Report($"Converting {game.Name} (CUE/BIN to CCD/IMG/SUB)...");
                await Cue2CcdConverter.ConvertAsync(cuePath, destFolder, progress);
            }
            else if (cueFile != null)
            {
                progress?.Report($"Converting {game.Name} (CUE/BIN to CCD/IMG/SUB)...");
                await Cue2CcdConverter.ConvertAsync(cueFile, destFolder, progress);
            }
            else
            {
                progress?.Report($"Copying {game.Name} to folder {game.FolderNumberFormatted}...");

                foreach (var file in extractedFiles)
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (Constants.AllImageExtensions.Contains(ext) || ext == ".sub" || ext == ".mdf")
                    {
                        string destFile = Path.Combine(destFolder, Path.GetFileName(file));
                        await Task.Run(() => File.Copy(file, destFile, overwrite: true));
                    }
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempExtractDir, recursive: true); } catch { }
        }
    }

    private async Task ConvertChdAndCopyAsync(TownsGame game, string destFolder, IProgress<string>? progress, string? tempFolderRoot = null)
    {
        string? chdPath = game.ImageFiles
            .FirstOrDefault(f => Path.GetExtension(f).Equals(".chd", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(chdPath) || !File.Exists(chdPath))
            throw new FileNotFoundException($"CHD file not found: {Path.GetFileName(chdPath)}");

        string tempRoot = !string.IsNullOrEmpty(tempFolderRoot) && Directory.Exists(tempFolderRoot)
            ? tempFolderRoot
            : Path.GetTempPath();
        string tempCueDir = Path.Combine(tempRoot,
            "FMSquared_chd_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var (success, message, cuePath) = await ChdConverter.ConvertToCueBinAsync(
                chdPath, tempCueDir, progress, gameName: game.Name);

            if (!success || cuePath == null)
                throw new InvalidOperationException(
                    $"CHD conversion failed for {game.Name}: {message}");

            progress?.Report($"Converting {game.Name} (CUE/BIN to CCD/IMG/SUB)...");
            await Cue2CcdConverter.ConvertAsync(cuePath, destFolder, progress);
        }
        finally
        {
            try { Directory.Delete(tempCueDir, recursive: true); } catch { }
        }
    }

    private TownsGame? LoadGameFromSource(string sourcePath, string? specificFile)
    {
        if (specificFile == null && !CardScanner.HasDiscImage(sourcePath))
            return null;

        // If a specific CUE file was selected, parse it to find related BIN files.
        bool isCueFile = specificFile != null &&
            Path.GetExtension(specificFile).Equals(".cue", StringComparison.OrdinalIgnoreCase);

        // If a specific CCD file was selected, find its companion .img and .sub files.
        bool isCcdFile = specificFile != null &&
            Path.GetExtension(specificFile).Equals(".ccd", StringComparison.OrdinalIgnoreCase);

        // If a specific MDS file was selected, find its companion .mdf file.
        bool isMdsFile = specificFile != null &&
            Path.GetExtension(specificFile).Equals(".mds", StringComparison.OrdinalIgnoreCase);

        List<string>? cueRelatedFiles = null;
        if (isCueFile)
        {
            cueRelatedFiles = CueSheetParser.GetAllRelatedFiles(specificFile!);
        }

        List<string>? companionFiles = null;
        if (isCcdFile)
        {
            companionFiles = new List<string> { specificFile! };
            string basePath = Path.Combine(Path.GetDirectoryName(specificFile!)!,
                                           Path.GetFileNameWithoutExtension(specificFile!));

            string imgFile = basePath + ".img";
            if (File.Exists(imgFile))
                companionFiles.Add(imgFile);

            string subFile = basePath + ".sub";
            if (File.Exists(subFile))
                companionFiles.Add(subFile);
        }
        else if (isMdsFile)
        {
            companionFiles = new List<string> { specificFile! };
            string mdfFile = Path.ChangeExtension(specificFile!, ".mdf");
            if (File.Exists(mdfFile))
                companionFiles.Add(mdfFile);
        }

        // Default the title to the base file name of the disc image itself.
        // When a whole folder was added, use its primary image file.
        string? primaryImage = specificFile;
        if (primaryImage == null)
        {
            var folderFiles = Directory.GetFiles(sourcePath);
            primaryImage =
                folderFiles.FirstOrDefault(f => Path.GetExtension(f).Equals(".cue", StringComparison.OrdinalIgnoreCase)) ??
                folderFiles.FirstOrDefault(f => Path.GetExtension(f).Equals(".ccd", StringComparison.OrdinalIgnoreCase)) ??
                folderFiles.FirstOrDefault(f => Constants.AllImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())) ??
                folderFiles.FirstOrDefault(f => Path.GetExtension(f).Equals(".chd", StringComparison.OrdinalIgnoreCase));
        }

        string displayName = primaryImage != null
            ? Path.GetFileNameWithoutExtension(primaryImage)
            : Path.GetFileName(sourcePath);

        var game = new TownsGame
        {
            Name = NameSanitizer.Sanitize(displayName),
            SourcePath = sourcePath
        };

        // A Title.txt alongside the disc image overrides the derived name.
        string titlePath = Path.Combine(sourcePath, Constants.TitleFile);
        if (File.Exists(titlePath))
        {
            string title = File.ReadAllText(titlePath).Trim();
            if (!string.IsNullOrWhiteSpace(title))
                game.Name = title;
        }

        // Track only the relevant files.
        long totalSize = 0;

        if (isCueFile && cueRelatedFiles != null)
        {
            // CUE file: only track the CUE and its referenced BINs.
            foreach (var file in cueRelatedFiles)
            {
                game.ImageFiles.Add(file);
                totalSize += new FileInfo(file).Length;
            }
            game.FileFormat = FileFormat.CueBin;
        }
        else if (companionFiles != null)
        {
            // CCD or MDS file: track the main file and its companions.
            foreach (var file in companionFiles)
            {
                game.ImageFiles.Add(file);
                totalSize += new FileInfo(file).Length;
            }
            game.FileFormat = isCcdFile ? FileFormat.CloneCd : FileFormat.Uncompressed;
        }
        else if (specificFile != null)
        {
            // Specific non-CUE/non-CCD file selected.
            game.ImageFiles.Add(specificFile);
            totalSize += new FileInfo(specificFile).Length;

            if (Path.GetExtension(specificFile).Equals(".chd", StringComparison.OrdinalIgnoreCase))
                game.FileFormat = FileFormat.Chd;
        }
        else
        {
            // Directory: collect all image files.
            foreach (var file in Directory.GetFiles(sourcePath))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (Constants.AllImageExtensions.Contains(ext) || ext == ".sub" || ext == ".mdf" || ext == ".chd")
                {
                    game.ImageFiles.Add(file);
                    totalSize += new FileInfo(file).Length;
                }
            }

            if (game.ImageFiles.Any(f => Path.GetExtension(f).Equals(".ccd", StringComparison.OrdinalIgnoreCase)))
                game.FileFormat = FileFormat.CloneCd;
            else if (game.ImageFiles.Any(f => Path.GetExtension(f).Equals(".cue", StringComparison.OrdinalIgnoreCase)))
                game.FileFormat = FileFormat.CueBin;
            else if (game.ImageFiles.Any(f => Path.GetExtension(f).Equals(".chd", StringComparison.OrdinalIgnoreCase)))
                game.FileFormat = FileFormat.Chd;
        }

        game.Length = totalSize;
        return game;
    }

    // The archive itself is stored as the source and extracted later during save,
    // so adding a game reads nothing but the entry table and a Title.txt entry.
    private TownsGame? LoadGameFromArchive(string archivePath, List<string> warnings)
    {
        var allEntries = ArchiveHelper.GetArchiveEntries(archivePath);
        var entries = FilterNormalizableEntries(allEntries);

        var imageEntries = entries.Where(e =>
        {
            var ext = GetEntryExtension(e);
            return Constants.AllImageExtensions.Contains(ext) || ext == ".chd";
        }).ToList();

        if (imageEntries.Count == 0)
            return null;

        var selected = SelectPrimaryImageEntry(imageEntries, out var innerFormat);

        var warning = BuildMultiImageWarning(archivePath, imageEntries, selected);
        if (warning != null)
            warnings.Add(warning);

        // The name comes from the entry that save-time extraction delivers,
        // so the title on the card always matches the disc in the folder.
        var game = new TownsGame
        {
            Name = NameSanitizer.Sanitize(
                Path.GetFileNameWithoutExtension(ArchiveEntryPath.GetLeafName(selected.FullName))),
            SourcePath = archivePath
        };

        // A Title.txt packed in the archive overrides the derived name.
        string? title = ReadArchiveTitleText(archivePath, entries, selected);
        if (!string.IsNullOrWhiteSpace(title))
            game.Name = title.Trim();

        game.InnerFileFormat = innerFormat;
        game.FileFormat = FileFormat.Compressed;
        game.SelectedArchiveEntry = selected;
        game.ImageFiles.Add(archivePath);

        // Show the uncompressed size so the user knows actual SD card
        // usage, measured from what save-time extraction will pull out.
        long length;
        try
        {
            length = ArchiveEntrySelection.SelectForFlatExtraction(allEntries, selected)
                .Where(IsSizeCountedEntry)
                .Sum(e => e.Size);
        }
        catch (InvalidDataException)
        {
            length = entries.Where(IsSizeCountedEntry).Sum(e => e.Size);
        }
        game.Length = length;

        return game;
    }

    /// <summary>
    /// Drops entries whose key cannot be normalized (rooted paths, keys
    /// escaping the archive root). Original ordinals are kept.
    /// </summary>
    private static IReadOnlyList<ArchiveEntryInfo> FilterNormalizableEntries(IReadOnlyList<ArchiveEntryInfo> allEntries)
    {
        var result = new List<ArchiveEntryInfo>();

        foreach (var entry in allEntries)
        {
            try
            {
                ArchiveEntryPath.NormalizeKey(entry.FullName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    private static string GetEntryExtension(ArchiveEntryInfo entry)
    {
        return Path.GetExtension(ArchiveEntryPath.GetLeafName(entry.FullName)).ToLowerInvariant();
    }

    /// <summary>
    /// Files whose sizes made up an archive row's Length before this port:
    /// everything the save-time copy step can put on the SD card.
    /// </summary>
    private static bool IsSizeCountedEntry(ArchiveEntryInfo entry)
    {
        var ext = GetEntryExtension(entry);
        return Constants.AllImageExtensions.Contains(ext) ||
               ext == ".sub" || ext == ".mdf" || ext == ".chd";
    }

    /// <summary>
    /// Picks the archive entry that anchors extraction, preferring set
    /// manifests over their data files, and reports the inner format the
    /// same way scanning an extracted folder would.
    /// </summary>
    private static ArchiveEntryInfo SelectPrimaryImageEntry(List<ArchiveEntryInfo> imageEntries, out FileFormat innerFormat)
    {
        var ccd = imageEntries.FirstOrDefault(e => GetEntryExtension(e) == ".ccd");
        if (ccd != null)
        {
            innerFormat = FileFormat.CloneCd;
            return ccd;
        }

        var cue = imageEntries.FirstOrDefault(e => GetEntryExtension(e) == ".cue");
        if (cue != null)
        {
            innerFormat = FileFormat.CueBin;
            return cue;
        }

        var chd = imageEntries.FirstOrDefault(e => GetEntryExtension(e) == ".chd");
        if (chd != null)
        {
            innerFormat = FileFormat.Chd;
            return chd;
        }

        innerFormat = FileFormat.Uncompressed;
        var mds = imageEntries.FirstOrDefault(e => GetEntryExtension(e) == ".mds");
        return mds ?? imageEntries[0];
    }

    private static string? BuildMultiImageWarning(string archivePath, List<ArchiveEntryInfo> imageEntries, ArchiveEntryInfo selected)
    {
        // Manifest-style formats each represent a disc image, with same-name
        // manifests (e.g., a ccd+cue rip of one game) counted once. A
        // standalone .iso counts too unless a selected cue may reference it
        // as track data. Plain data files (.img, .mdf, .bin) never count,
        // since they are usually companions.
        var manifestExtensions = new[] { ".ccd", ".cue", ".chd", ".cdi", ".mds" };
        string selectedExtension = GetEntryExtension(selected);

        int imageCount = imageEntries
            .Where(e => manifestExtensions.Contains(GetEntryExtension(e)))
            .Select(e => Path.GetFileNameWithoutExtension(ArchiveEntryPath.GetLeafName(e.FullName)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (imageCount > 0 && selectedExtension != ".cue")
            imageCount += imageEntries.Count(e => GetEntryExtension(e) == ".iso");

        if (imageCount == 0)
            imageCount = imageEntries.Count;
        if (imageCount <= 1)
            return null;

        return $"Archive \"{Path.GetFileName(archivePath)}\" contains {imageCount} disc images. " +
               $"Only \"{ArchiveEntryPath.GetLeafName(selected.FullName)}\" will be added.";
    }

    private static string? ReadArchiveTitleText(string archivePath, IReadOnlyList<ArchiveEntryInfo> entries, ArchiveEntryInfo selected)
    {
        // Sidecar text files are tiny. Anything bigger is not a sidecar.
        const long maxTitleBytes = 4096;

        // Only a Title.txt stored beside the selected image or at the
        // archive root applies, so another game's title cannot bleed in.
        // The image's own directory wins over the root.
        string selectedDir = ArchiveEntryPath.GetDirectoryKey(selected.FullName);

        ArchiveEntryInfo? match = null;
        foreach (var entry in entries)
        {
            if (!string.Equals(ArchiveEntryPath.GetLeafName(entry.FullName), Constants.TitleFile, StringComparison.OrdinalIgnoreCase))
                continue;

            string directory = ArchiveEntryPath.GetDirectoryKey(entry.FullName);
            bool inSelectedDirectory = selectedDir.Length > 0 &&
                string.Equals(directory, selectedDir, StringComparison.OrdinalIgnoreCase);
            if (inSelectedDirectory)
            {
                match = entry;
                break;
            }
            if (directory.Length == 0)
                match ??= entry;
        }

        if (match == null)
            return null;

        var bytes = ArchiveHelper.ReadArchiveEntryBytes(archivePath, match, maxTitleBytes);
        if (bytes == null)
            return null;

        // File.ReadAllText detects UTF-8 and UTF-16 byte order marks, so
        // match it.
        using var textReader = new StreamReader(
            new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return textReader.ReadToEnd();
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
