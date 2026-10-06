using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    public ObservableCollection<TownsGame> ItemList { get; }

    /// <summary>
    /// Manages undo/redo operations.
    /// </summary>
    public UndoManager UndoManager { get; }

    private int _operationActive;
    private int _mutationThread;
    private bool _needsRecovery;
    public bool IsOperationActive => Volatile.Read(ref _operationActive) != 0;
    public bool NeedsRecovery => _needsRecovery;
    public bool CanEdit => !IsOperationActive && !NeedsRecovery;

    public Manager()
    {
        ItemList = new GuardedGameCollection(EnsureCanMutate, NotifyObservers);
        UndoManager = new UndoManager(EnsureCanMutate, () => CanEdit, NotifyObservers);
    }

    private void EnsureCanMutate()
    {
        if (_mutationThread == Environment.CurrentManagedThreadId) return;
        if (!CanEdit) throw new InvalidOperationException("Finish the current operation or recover the card before editing.");
    }

    private void Mutate(Action action)
    {
        int previous = _mutationThread;
        _mutationThread = Environment.CurrentManagedThreadId;
        try { action(); }
        finally { _mutationThread = previous; }
    }

    private void NotifyObservers(Action action)
    {
        int previous = _mutationThread;
        _mutationThread = 0;
        try { action(); }
        finally { _mutationThread = previous; }
    }

    private async Task<T> RunOperationAsync<T>(Func<Task<T>> action)
    {
        if (Interlocked.CompareExchange(ref _operationActive, 1, 0) != 0)
            throw new InvalidOperationException("Another card operation is already active.");
        try { NotifyOperationState(); return await action(); }
        finally { Volatile.Write(ref _operationActive, 0); NotifyOperationState(); }
    }

    private async Task RunOperationAsync(Func<Task> action) =>
        await RunOperationAsync(async () => { await action(); return true; });

    private void NotifyOperationState()
    {
        OnPropertyChanged(nameof(IsOperationActive));
        OnPropertyChanged(nameof(NeedsRecovery));
        OnPropertyChanged(nameof(CanEdit));
        UndoManager.NotifyEligibilityChanged();
    }

    // On-card items removed from the list. Their folders are deleted on save.
    private readonly List<TownsGame> _removedItems = new();

    /// <summary>
    /// Path to the SD card root (e.g., "H:\").
    /// </summary>
    public string SdCardPath
    {
        get => _sdCardPath;
        set
        {
            if (IsOperationActive) throw new InvalidOperationException("Another card operation is already active.");
            if (_sdCardPath == value) return;
            _sdCardPath = value;
            _needsRecovery = false;
            Mutate(() => { ItemList.Clear(); _removedItems.Clear(); UndoManager.Clear(); });
            OnPropertyChanged();
            NotifyOperationState();
        }
    }

    /// <summary>
    /// The ODE type detected on the current SD card.
    /// </summary>
    public OdeKind OdeKindDetected
    {
        get => _odeKindDetected;
        set { EnsureCanMutate(); _odeKindDetected = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The menu type the user has selected (Almanac for DocBrown,
    /// Spellbook for Wizard). Defaults to the detected type after loading.
    /// </summary>
    public OdeKind OdeKindSelected
    {
        get => _odeKindSelected;
        set { EnsureCanMutate(); _odeKindSelected = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Path to the application's tools directory, which holds the bundled
    /// Almanac and Spellbook menu file sets.
    /// </summary>
    private string _toolsPath = string.Empty;
    public string ToolsPath { get => _toolsPath; set { if (_toolsPath == value) return; EnsureCanMutate(); _toolsPath = value; } }

    /// <summary>
    /// Whether to run lock checks before save operations.
    /// </summary>
    private bool _enableLockCheck = true;
    public bool EnableLockCheck { get => _enableLockCheck; set { EnsureCanMutate(); _enableLockCheck = value; } }

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
    public Task LoadItemsFromCardAsync() => RunOperationAsync(() => LoadItemsFromCardCoreAsync());

    private async Task LoadItemsFromCardCoreAsync()
    {
        if (string.IsNullOrEmpty(SdCardPath) || !Directory.Exists(SdCardPath))
            throw new InvalidOperationException("Invalid SD card path.");
        await RecoverCardCoreAsync();
        var tx = OpenSave();
        var loaded = new List<TownsGame>();
        await CardScanner.ScanCardAsync(SdCardPath, loaded.Add);
        if (tx != null)
        {
            var ordered = new List<TownsGame>();
            foreach (var item in tx.Items)
            {
                TownsGame? game;
                if (item.OriginalName != null)
                    game = loaded.SingleOrDefault(g => string.Equals(Path.GetFileName(g.FullFolderPath), item.OriginalName, StringComparison.OrdinalIgnoreCase));
                else
                {
                    var row = item.Row ?? throw new InvalidDataException("Prepared import has no recorded source row: " + item.Id);
                    game = new TownsGame
                    {
                        Name = row.Name,
                        SourcePath = row.SourcePath,
                        FileFormat = row.Format,
                        InnerFileFormat = row.InnerFormat,
                        SelectedArchiveEntry = row.ArchiveEntry,
                        ImageFiles = new Collection<string>(row.ImageFiles.ToList()),
                        Length = row.Length,
                        IsPlaceholder = row.Placeholder,
                        WorkMode = WorkMode.New,
                        TitleDirty = true
                    };
                }
                if (game == null) throw new InvalidDataException("Recorded game is missing: " + item.Id);
                game.SaveId = item.Id;
                if (item.Row != null) { game.Name = item.Row.Name; game.TitleDirty = true; }
                ordered.Add(game);
                loaded.Remove(game);
            }
            ordered.AddRange(loaded);
            loaded = ordered;
        }
        var detected = await Task.Run(() => OdeDetector.Detect(SdCardPath));
        Mutate(() =>
        {
            OdeKindDetected = detected;
            OdeKindSelected = tx == null ? OdeKindDetected : Enum.Parse<OdeKind>(tx.SelectedOde);
            ItemList.Clear();
            _removedItems.Clear();
            UndoManager.Clear();
            ItemList.Add(new TownsGame { Name = "MENU", SdNumber = 1, FullFolderPath = Path.Combine(SdCardPath, "01"), Length = -1 });
            foreach (var game in loaded) ItemList.Add(game);
        });
    }

    /// <summary>
    /// Sorts the item list alphabetically by title (menu item stays first),
    /// recording the change for undo.
    /// </summary>
    public void SortList()
    {
        EnsureCanMutate();
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

    public Func<string, Task<bool>>? OnDiscardIncompletePreparation { get; set; }

    public string? LastSaveOutcome { get; private set; }

    internal Func<string, (string Volume, long Available)> SpaceProbe { get; set; } = GetVolumeSpace;

    private CardSaveTransaction? OpenSave() => CardSaveTransaction.Open(SdCardPath,
        (from, to) => FolderHelper.MoveDirectoryAsync(from, to, OnFolderLocked));

    public Task RecoverCardAsync() => RunOperationAsync(() => RecoverCardCoreAsync());

    private async Task RecoverCardCoreAsync()
    {
        try
        {
            var tx = OpenSave();
            if (tx != null)
            {
                bool committed = tx.HasCommitIntent;
                await tx.RecoverAsync();
                if (committed)
                    Mutate(() =>
                    {
                        ApplyPublishedRows(tx.Items);
                        _removedItems.RemoveAll(game => !tx.Items.Any(item => item.Id == game.SaveId));
                        UndoManager.Clear();
                    });
            }
            _needsRecovery = false;
        }
        catch { _needsRecovery = true; throw; }
    }

    /// <summary>
    /// Saves all changes to the SD card: renumbers folders to match the list
    /// order, rewrites FAT directory order via the temp-folder shuffle, copies
    /// new items, writes Title.txt/GameList.txt, and rebuilds the menu ISO.
    /// </summary>
    public Task SaveAsync(IProgress<string>? progress = null, IProgress<int>? itemProgress = null, string? tempFolderRoot = null) => RunOperationAsync(() => SaveCoreAsync(progress, itemProgress, tempFolderRoot));

    private async Task SaveCoreAsync(IProgress<string>? progress = null, IProgress<int>? itemProgress = null, string? tempFolderRoot = null)
    {
        if (string.IsNullOrEmpty(SdCardPath))
            throw new InvalidOperationException("No SD card path set.");
        LastSaveOutcome = null;
        await RecoverCardCoreAsync();
        string root = Path.GetFullPath(SdCardPath);
        string tools = ToolsPath;
        OdeKind ode = OdeKindSelected;
        var rows = ItemList.Where(g => !g.IsMenuItem).ToList();
        var planned = rows.Select((g, i) => Snapshot(g, i + 2)).ToList();
        var removals = _removedItems.Where(g => !rows.Contains(g)).ToList();
        ValidateSavePaths(root, planned, removals);
        var mappings = planned.Select(g => new CardSaveItem(g.SaveId,
            g.WorkMode == WorkMode.New ? null : Path.GetFileName(g.FullFolderPath), g.FolderNumberFormatted)
        { Row = CaptureRow(g) }).ToList();
        var tx = OpenSave();
        string tempRoot = Path.GetFullPath(!string.IsNullOrEmpty(tempFolderRoot) && Directory.Exists(tempFolderRoot)
            ? tempFolderRoot : Path.GetTempPath());
        ValidateTempPath(root, tempRoot, planned, removals);
        foreach (var game in planned.Where(g => g.WorkMode == WorkMode.New && g.FileFormat == FileFormat.Compressed))
        {
            if (tx?.Items.Any(x => x.Id == game.SaveId && x.Ready) == true) continue;
            if (game.SelectedArchiveEntry != null)
                ArchiveEntrySelection.SelectForFlatExtraction(ArchiveHelper.GetArchiveEntries(game.SourcePath), game.SelectedArchiveEntry);
        }
        if (tx == null)
        {
            tx = CardSaveTransaction.Begin(root, ode.ToString(), mappings,
                (from, to) => FolderHelper.MoveDirectoryAsync(from, to, OnFolderLocked),
                removals.Where(g => g.WorkMode != WorkMode.New && Directory.Exists(g.FullFolderPath)).Select(g => Path.GetFileName(g.FullFolderPath)));
            await tx.RecoverAsync();
        }
        else
        {
            foreach (var incomplete in tx.GetIncompletePreparations())
            {
                if (incomplete.Value.Count != 0 && (OnDiscardIncompletePreparation == null ||
                    !await OnDiscardIncompletePreparation(incomplete.Key)))
                    throw new OperationCanceledException("Incomplete preparation preserved: " + incomplete.Key);
                tx.DiscardIncompletePreparation(incomplete.Key, incomplete.Value);
            }
            tx.Replan(ode.ToString(), mappings, removals.Select(g => g.SaveId).ToArray());
        }

        int processed = 0;
        foreach (var game in planned)
        {
            var item = tx.Items.Single(x => x.Id == game.SaveId);
            if (item.OriginalName == null && !item.Ready)
            {
                progress?.Report($"Preparing {game.Name}...");
                string destination = tx.GetImportPath(item.Id);
                var expected = await PrepareImportAsync(game, destination, tx, tempRoot, progress);
                File.WriteAllText(Path.Combine(destination, Constants.TitleFile), game.Name, new UTF8Encoding(false));
                expected[Constants.TitleFile] = Encoding.UTF8.GetByteCount(game.Name);
                tx.MarkReady(item.Id, expected, game.IsPlaceholder, requireSub: false);
            }
            itemProgress?.Report(++processed);
        }

        var metadata = new List<(string Target, string? Source, string? Text)>();
        foreach (var game in planned)
        {
            var item = tx.Items.Single(x => x.Id == game.SaveId);
            string current = Path.Combine(item.OriginalName == null ? tx.GetImportPath(item.Id) : Path.Combine(root, item.OriginalName), Constants.TitleFile);
            if ((!item.Ready && game.TitleDirty) || !File.Exists(current) || (item.Ready && File.ReadAllText(current) != game.Name))
                metadata.Add((game.FolderNumberFormatted + "/" + Constants.TitleFile, null, game.Name));
        }
        var list = new StringBuilder("01 - MENU\r\n");
        foreach (var game in planned) list.Append(game.FolderNumberFormatted).Append(" - ").Append(game.Name).Append("\r\n");
        metadata.Add((Constants.GameListFile, null, list.ToString()));
        bool menuBuilt = await PrepareMenuAsync(root, tools, ode, planned, tx, metadata, progress);
        var paths = tx.AllocateMetadata(metadata.Select((_, i) => "Metadata-" + i).ToArray());
        var actions = new List<(string Target, string? Prepared)>();
        for (int i = 0; i < metadata.Count; i++)
        {
            var entry = metadata[i];
            if (entry.Text != null) File.WriteAllText(paths[i], entry.Text, new UTF8Encoding(false));
            else if (entry.Source != null) File.Move(entry.Source, paths[i]);
            else { actions.Add((entry.Target, null)); continue; }
            actions.Add((entry.Target, paths[i]));
        }
        tx.RecordMetadataBatch(actions, paths.Where((_, i) => metadata[i].Text == null && metadata[i].Source == null).ToArray());
        if (menuBuilt) tx.SealWorkDirectory("Menu");
        progress?.Report("Publishing game folders in menu order...");
        await tx.PublishAsync();
        progress?.Report("Committing menu and metadata...");
        await tx.CommitAsync();
        Mutate(() =>
        {
            ApplyPublishedRows(tx.Items);
            _removedItems.Clear();
            OdeKindDetected = ode;
            UndoManager.Clear();
        });
        LastSaveOutcome = menuBuilt ? "Saved and rebuilt the menu ISO." : "Saved. Menu ISO was not rebuilt because the menu files are missing.";
        progress?.Report(LastSaveOutcome);
    }

    private static TownsGame Snapshot(TownsGame game, int number) => new()
    {
        SaveId = game.SaveId,
        Name = game.Name,
        SdNumber = number,
        WorkMode = game.WorkMode,
        FullFolderPath = game.FullFolderPath,
        SourcePath = game.SourcePath,
        FileFormat = game.FileFormat,
        InnerFileFormat = game.InnerFileFormat,
        SelectedArchiveEntry = game.SelectedArchiveEntry,
        ImageFiles = new Collection<string>(game.ImageFiles.ToList()),
        Length = game.Length,
        IsPlaceholder = game.IsPlaceholder,
        TitleDirty = game.TitleDirty
    };

    private static CardSaveRow CaptureRow(TownsGame game) => new(game.Name, game.SourcePath, game.FileFormat,
        game.InnerFileFormat, game.SelectedArchiveEntry, game.ImageFiles.ToList(), game.Length, game.IsPlaceholder);

    private void ApplyPublishedRows(IReadOnlyList<CardSaveItem> items)
    {
        foreach (var item in items)
        {
            var game = ItemList.Concat(_removedItems).FirstOrDefault(g => g.SaveId == item.Id);
            if (game == null) continue;
            game.SdNumber = int.Parse(item.FinalName);
            game.FullFolderPath = Path.Combine(SdCardPath, item.FinalName);
            game.WorkMode = WorkMode.None;
            game.TitleDirty = item.Row != null && game.Name != item.Row.Name;
            game.ImageFiles = new Collection<string>(item.Files!.Keys.Where(IsImageName).Select(p => Path.Combine(game.FullFolderPath, p)).ToList());
            game.Length = game.IsPlaceholder ? -1 : game.ImageFiles.Sum(p => new FileInfo(p).Length);
            game.FileFormat = game.ImageFiles.Any(p => Path.GetExtension(p).Equals(".ccd", StringComparison.OrdinalIgnoreCase))
                ? FileFormat.CloneCd : FileFormat.Uncompressed;
            game.InnerFileFormat = null;
            game.SelectedArchiveEntry = null;
        }
    }

    private static bool IsImageName(string name)
    {
        string extension = Path.GetExtension(name).ToLowerInvariant();
        return Constants.AllImageExtensions.Contains(extension) || extension is ".sub" or ".mdf" or ".chd";
    }

    private static void ValidateSavePaths(string root, List<TownsGame> games, List<TownsGame> removed)
    {
        var originals = games.Concat(removed).Distinct().Where(g => g.WorkMode != WorkMode.New && !string.IsNullOrEmpty(g.FullFolderPath)).ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in originals)
        {
            string path = Path.GetFullPath(game.FullFolderPath);
            if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) || !names.Add(path))
                throw new InvalidDataException("Overlapping or non-root game folder: " + path);
        }
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(directory);
            if (int.TryParse(name, out int n) && n != 1 && !names.Contains(directory))
                throw new InvalidDataException("Unowned numbered folder requires review: " + directory);
        }
        var affected = originals.Select(g => Path.GetFullPath(g.FullFolderPath)).Concat(new[] { Path.Combine(root, "01"), Path.Combine(root, CardSaveTransaction.WorkName) });
        foreach (var game in games.Where(g => g.WorkMode == WorkMode.New))
            foreach (string source in game.ImageFiles.Append(game.SourcePath).Where(p => !string.IsNullOrEmpty(p)))
                foreach (string path in affected)
                    if (string.Equals(Path.GetFullPath(source), path, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFullPath(source).StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Import source overlaps a card operation: " + source);
    }

    private static void ValidateTempPath(string root, string temp, List<TownsGame> games, List<TownsGame> removed)
    {
        var protectedPaths = games.Concat(removed).Where(g => !string.IsNullOrEmpty(g.FullFolderPath)).Select(g => g.FullFolderPath)
            .Concat(games.SelectMany(g => g.ImageFiles))
            .Concat(new[] { Path.Combine(root, "01"), Path.Combine(root, CardSaveTransaction.WorkName) });
        foreach (string path in protectedPaths)
            if (string.Equals(temp, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) ||
                temp.StartsWith(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Temporary path overlaps card or source data: " + temp);
    }

    private async Task<Dictionary<string, long>> PrepareImportAsync(TownsGame game, string destination, CardSaveTransaction tx, string tempRoot, IProgress<string>? progress)
    {
        if (game.IsPlaceholder) return new(StringComparer.OrdinalIgnoreCase);
        string? work = null;
        bool onCard = string.Equals(tempRoot, Path.GetFullPath(SdCardPath), StringComparison.OrdinalIgnoreCase) ||
            tempRoot.StartsWith(Path.GetFullPath(SdCardPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        string GetWork()
        {
            if (onCard) return tx.GetWorkDirectory(game.SaveId);
            string path = Path.Combine(tempRoot, "FMSquared_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
        try
        {
            var files = game.ImageFiles.ToList();
            var format = game.FileFormat;
            if (game.FileFormat == FileFormat.Compressed)
            {
                var entries = ArchiveHelper.GetArchiveEntries(game.SourcePath);
                var selected = game.SelectedArchiveEntry ?? SelectPrimaryImageEntry(entries.Where(e => IsImageName(e.FullName)).ToList(), out _);
                var expected = ArchiveEntrySelection.SelectForFlatExtraction(entries, selected);
                work = GetWork();
                string extracted = await Task.Run(() => ArchiveHelper.ExtractArchiveForEntry(game.SourcePath, work, selected));
                foreach (var entry in expected)
                {
                    string path = Path.Combine(work, ArchiveEntryPath.GetLeafName(entry.FullName));
                    if (!File.Exists(path) || new FileInfo(path).Length != entry.Size)
                        throw new InvalidDataException("Incomplete extracted file: " + path);
                }
                var selectedGame = LoadGameFromSource(work, extracted)!;
                files = selectedGame.ImageFiles.ToList();
                format = selectedGame.FileFormat;
            }
            else if (format == FileFormat.CloneCd &&
                files.FirstOrDefault(f => Path.GetExtension(f).Equals(".ccd", StringComparison.OrdinalIgnoreCase)) is string selectedCcd)
            {
                var selectedFiles = new[] { selectedCcd, Path.ChangeExtension(selectedCcd, ".img"), Path.ChangeExtension(selectedCcd, ".sub") };
                files = files.Where(f => selectedFiles.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
            }
            string? chd = format == FileFormat.Chd
                ? files.FirstOrDefault(f => Path.GetExtension(f).Equals(".chd", StringComparison.OrdinalIgnoreCase)) : null;
            string? cue = format == FileFormat.CueBin
                ? files.FirstOrDefault(f => Path.GetExtension(f).Equals(".cue", StringComparison.OrdinalIgnoreCase)) : null;
            if (chd != null)
            {
                work ??= GetWork();
                var converted = await ChdConverter.ConvertToCueBinAsync(chd, Path.Combine(work, "Cue"), progress, gameName: game.Name);
                if (!converted.Success || converted.CuePath == null) throw new InvalidOperationException("CHD conversion failed: " + converted.Message);
                cue = converted.CuePath;
            }
            Dictionary<string, long> output;
            if (cue != null)
            {
                output = Cue2CcdConverter.ExpectedOutput(cue);
                if (!await Cue2CcdConverter.ConvertAsync(cue, destination, progress))
                    throw new InvalidDataException("CUE conversion failed: " + cue);
            }
            else
            {
                if (format is FileFormat.CueBin or FileFormat.Chd || files.Count == 0)
                    throw new InvalidDataException("The selected image is missing: " + game.Name);
                output = files.ToDictionary(p => Path.GetFileName(p)!, p => new FileInfo(p).Length, StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    string extension = Path.GetExtension(file).ToLowerInvariant();
                    if (extension is ".ccd" or ".mds")
                    {
                        string companion = Path.ChangeExtension(Path.GetFileName(file), extension == ".ccd" ? ".img" : ".mdf");
                        if (!output.TryGetValue(companion, out long length) || length <= 0)
                            throw new InvalidDataException("Missing image companion: " + companion);
                    }
                }
                foreach (string file in files) await Task.Run(() => File.Copy(file, Path.Combine(destination, Path.GetFileName(file))));
            }
            if (work != null && onCard) tx.SealWorkDirectory(game.SaveId);
            return output;
        }
        finally
        {
            if (work != null && !onCard)
            {
                try { Directory.Delete(work, recursive: true); }
                catch (IOException) { progress?.Report("Temporary conversion files remain at " + work); }
                catch (UnauthorizedAccessException) { progress?.Report("Temporary conversion files remain at " + work); }
            }
        }
    }

    private static async Task<bool> PrepareMenuAsync(string root, string tools, OdeKind ode, List<TownsGame> games,
        CardSaveTransaction tx, List<(string Target, string? Source, string? Text)> metadata, IProgress<string>? progress)
    {
        OdeKind oldOde = OdeDetector.Detect(root);
        bool switching = oldOde != OdeKind.Unknown && ode != OdeKind.Unknown && oldOde != ode;
        string assets = Path.Combine(tools, ode == OdeKind.Wizard ? "spellbook" : "almanac");
        bool bundled = !string.IsNullOrEmpty(tools) && File.Exists(Path.Combine(assets, Constants.MenuBootFileName)) &&
            File.Exists(Path.Combine(assets, Constants.MenuDataFolderName, Constants.MenuIoSysName));
        if (switching && !bundled) throw new InvalidOperationException("The selected menu's bundled files are missing.");
        if (!MenuIsoBuilder.CanBuild(root) && !bundled) return false;
        string source = switching || !MenuIsoBuilder.CanBuild(root) ? assets : Path.Combine(root, "01");
        string preparedRoot = tx.GetWorkDirectory("Menu");
        string output = Path.Combine(preparedRoot, "01");
        Directory.CreateDirectory(output);
        progress?.Report("Preparing menu ISO...");
        var sources = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(p =>
        {
            string relative = Path.GetRelativePath(source, p).Replace('\\', '/');
            return relative.Equals(Constants.MenuBootFileName, StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(Constants.MenuDataFolderName + "/", StringComparison.OrdinalIgnoreCase);
        }).ToList();
        foreach (string file in sources)
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(output, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string configured = PathCasing.ResolveActualPath(Path.Combine(root, "01", relative));
            File.Copy(Path.GetExtension(file).Equals(".ini", StringComparison.OrdinalIgnoreCase) && File.Exists(configured)
                ? configured : file, target);
        }
        await Task.Run(() => MenuIsoBuilder.Build(preparedRoot, ode, games));
        foreach (string file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
            metadata.Add(("01/" + Path.GetRelativePath(output, file).Replace('\\', '/'), file, null));
        if (switching)
        {
            string oldAssets = Path.Combine(tools, oldOde == OdeKind.Wizard ? "spellbook" : "almanac");
            var oldNames = Directory.Exists(oldAssets)
                ? Directory.EnumerateFiles(oldAssets, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(oldAssets, p).Replace('\\', '/')).ToList()
                : new List<string>();
            oldNames.Add(Constants.MenuDataFolderName + "/" + (oldOde == OdeKind.Wizard ? "SPLLBOOK.EXP" : "ALMANAC.EXP"));
            foreach (string oldName in oldNames.Distinct(StringComparer.OrdinalIgnoreCase))
                if (oldName.StartsWith(Constants.MenuDataFolderName + "/", StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetExtension(oldName).Equals(".ini", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(root, "01", oldName)) && !metadata.Any(x => x.Target.Equals("01/" + oldName, StringComparison.OrdinalIgnoreCase)))
                    metadata.Add(("01/" + oldName, null, null));
        }
        string otherIso = ode == OdeKind.Wizard ? Constants.DocBrownMenuIsoName : Constants.WizardMenuIsoName;
        if (File.Exists(Path.Combine(root, "01", otherIso))) metadata.Add(("01/" + otherIso, null, null));
        string ini = ode == OdeKind.Wizard ? Constants.WizardIniFile : Constants.DocBrownIniFile;
        string defaultIni = Path.Combine(tools, "defaults", ini);
        if (!PathCasing.FileExistsIgnoreCase(Path.Combine(root, ini)) && File.Exists(defaultIni))
        {
            string preparedIni = Path.Combine(preparedRoot, ini);
            File.Copy(defaultIni, preparedIni);
            metadata.Add((ini, preparedIni, null));
        }
        return true;
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
    public Task<List<TownsGame>> AddGamesAsync(string[] paths, IProgress<string>? progress = null, int insertIndex = -1) => RunOperationAsync(() => AddGamesCoreAsync(paths, progress, insertIndex));

    private async Task<List<TownsGame>> AddGamesCoreAsync(string[] paths, IProgress<string>? progress = null, int insertIndex = -1)
    {
        if (NeedsRecovery) throw new InvalidOperationException("Recover the card before adding games.");
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

                Mutate(() =>
                {
                    if (insertIndex >= 0 && insertIndex <= ItemList.Count)
                    {
                        ItemList.Insert(insertIndex, game);
                        insertIndex++;
                    }
                    else
                    {
                        ItemList.Add(game);
                    }
                });

                added.Add(game);
            }
        }

        if (added.Count > 0)
        {
            var undoOp = new MultiItemAddOperation
            {
                ItemList = ItemList,
                OnItemRemoved = item => { if (!_removedItems.Contains(item)) _removedItems.Add(item); },
                OnItemRestored = item => _removedItems.Remove(item)
            };
            foreach (var game in added)
                undoOp.Items.Add((game, ItemList.IndexOf(game)));
            Mutate(() => UndoManager.RecordChange(undoOp));
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
        EnsureCanMutate();
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

        var undoOp = new MultiItemAddOperation
        {
            ItemList = ItemList,
            OnItemRemoved = item => { if (!_removedItems.Contains(item)) _removedItems.Add(item); },
            OnItemRestored = item => _removedItems.Remove(item)
        };
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
        EnsureCanMutate();
        var toRemove = items.Where(i => !i.IsMenuItem).ToList();
        if (toRemove.Count == 0) return;

        var undoOp = new MultiItemRemoveOperation
        {
            ItemList = ItemList,
            OnItemRestored = item => _removedItems.Remove(item),
            OnItemRemoved = item =>
            {
                if (!_removedItems.Contains(item))
                    _removedItems.Add(item);
            }
        };
        foreach (var item in toRemove)
            undoOp.Items.Add((item, ItemList.IndexOf(item)));

        foreach (var item in toRemove)
        {
            ItemList.Remove(item);

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
    public Task<SpaceCheckResult> CalculateRequiredSpaceAsync(string? tempFolderRoot = null) => RunOperationAsync(() => CalculateRequiredSpaceCoreAsync(tempFolderRoot));

    private async Task<SpaceCheckResult> CalculateRequiredSpaceCoreAsync(string? tempFolderRoot = null)
    {
        var result = new SpaceCheckResult { MetadataBuffer = 1024 * 1024 };
        if (string.IsNullOrEmpty(SdCardPath) || !Directory.Exists(SdCardPath))
        {
            result.HasSufficientSpace = true;
            return result;
        }
        await RecoverCardCoreAsync();
        string root = Path.GetFullPath(SdCardPath);
        var rows = ItemList.Where(g => !g.IsMenuItem).Select(g => Snapshot(g, g.SdNumber)).ToList();
        var removed = _removedItems.Where(g => !ItemList.Contains(g)).ToList();
        ValidateSavePaths(root, rows, removed);
        var ready = OpenSave()?.Items.Where(x => x.Ready).Select(x => x.Id).ToHashSet() ?? new();
        string tempRoot = Path.GetFullPath(!string.IsNullOrEmpty(tempFolderRoot) && Directory.Exists(tempFolderRoot)
            ? tempFolderRoot : Path.GetTempPath());
        ValidateTempPath(root, tempRoot, rows, removed);
        bool onCard = string.Equals(tempRoot, root, StringComparison.OrdinalIgnoreCase) ||
            tempRoot.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        string assets = GetMenuAssetPath(OdeKindSelected);
        bool switching = OdeKindDetected != OdeKind.Unknown && OdeKindSelected != OdeKind.Unknown && OdeKindDetected != OdeKindSelected;
        if (!switching && MenuIsoBuilder.CanBuild(root)) assets = Path.Combine(root, "01");
        await Task.Run(() =>
        {
            var cardVolume = SpaceProbe(root);
            var temporaryVolume = SpaceProbe(tempRoot);
            result.TemporaryFolderPath = tempRoot;
            result.TemporarySharesCardVolume = string.Equals(cardVolume.Volume, temporaryVolume.Volume, StringComparison.OrdinalIgnoreCase);
            result.AvailableSpace = result.TemporarySharesCardVolume
                ? Math.Min(cardVolume.Available, temporaryVolume.Available) : cardVolume.Available;
            result.TemporaryAvailableSpace = temporaryVolume.Available;
            result.MenuFolderExists = Directory.Exists(Path.Combine(root, "01"));
            long menuAssets = !string.IsNullOrEmpty(assets) && Directory.Exists(Path.Combine(assets, Constants.MenuDataFolderName))
                ? GetDirectorySize(Path.Combine(assets, Constants.MenuDataFolderName)) : 0;
            string boot = Path.Combine(assets, Constants.MenuBootFileName);
            if (File.Exists(boot)) menuAssets += new FileInfo(boot).Length;
            result.MenuSpaceNeeded = menuAssets == 0 ? 0 : menuAssets * 2 + 5L * 1024 * 1024;
            foreach (var game in removed.Where(g => g.WorkMode != WorkMode.New && Directory.Exists(g.FullFolderPath)))
                result.SpaceToBeFreed += GetDirectorySize(game.FullFolderPath);
            long preparationPeak = 0;
            long retainedWork = 0;
            long externalWorkPeak = 0;
            foreach (var game in rows)
            {
                if (game.WorkMode != WorkMode.New || game.IsPlaceholder || ready.Contains(game.SaveId)) continue;
                result.NewItemCount++;
                long size = Math.Max(game.Length, 0);
                var format = game.FileFormat == FileFormat.Compressed ? game.InnerFileFormat ?? FileFormat.Uncompressed : game.FileFormat;
                long intermediate = game.FileFormat == FileFormat.Compressed ? size : 0;
                if (game.FileFormat == FileFormat.Compressed) result.ContainsEstimatedSizes = true;
                if (format == FileFormat.CueBin)
                {
                    size = (long)(size * 1.25);
                    result.ContainsEstimatedSizes = true;
                }
                else if (format == FileFormat.Chd)
                {
                    size *= 2;
                    if (game.FileFormat != FileFormat.Compressed)
                    {
                        string? chdPath = game.ImageFiles.FirstOrDefault(p => Path.GetExtension(p).Equals(".chd", StringComparison.OrdinalIgnoreCase));
                        if (chdPath != null)
                        {
                            using var chd = new ChdReader(chdPath);
                            size = chd.Tracks.Sum(t => Math.Max(0L, t.Frames - t.Pad - t.Pregap) * 2448L) + 4096;
                        }
                    }
                    intermediate += size;
                    result.ContainsEstimatedSizes = true;
                }
                result.NewItemsSize += size;
                if (onCard) retainedWork += intermediate;
                else externalWorkPeak = Math.Max(externalWorkPeak, intermediate);
                preparationPeak = Math.Max(preparationPeak, result.NewItemsSize + retainedWork +
                    (!onCard && result.TemporarySharesCardVolume ? intermediate : 0));
            }
            long cardOutput = result.NewItemsSize + result.MenuSpaceNeeded + result.MetadataBuffer;
            result.TotalNeeded = Math.Max(cardOutput + retainedWork, preparationPeak + result.MetadataBuffer);
            result.TemporarySpaceNeeded = result.TotalNeeded - cardOutput;
            result.ExternalTemporarySpaceNeeded = result.TemporarySharesCardVolume ? 0 : externalWorkPeak;
            result.TemporaryShortfall = Math.Max(0, result.ExternalTemporarySpaceNeeded - result.TemporaryAvailableSpace);
            result.EffectiveAvailable = result.AvailableSpace + result.SpaceToBeFreed;
            result.Shortfall = result.TotalNeeded - result.EffectiveAvailable;
            result.HasSufficientSpace = result.Shortfall <= 0 && result.TemporaryShortfall == 0;
        });
        return result;
    }

    /// <summary>
    /// Builds the warning text shown when the space check comes up short.
    /// </summary>
    public static string BuildSpaceWarningMessage(SpaceCheckResult spaceCheck)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Insufficient free space for this save.\n");
        sb.AppendLine("Space needed:");
        sb.AppendLine($"  • New disc images ({spaceCheck.NewItemCount}): {FormatBytes(spaceCheck.NewItemsSize)}");
        sb.AppendLine($"  • Menu files: ~{FormatBytes(spaceCheck.MenuSpaceNeeded)}");
        sb.AppendLine($"  • Metadata files: ~{FormatBytes(spaceCheck.MetadataBuffer)}");
        if (spaceCheck.TemporarySpaceNeeded > 0)
            sb.AppendLine($"  Temporary conversion files: ~{FormatBytes(spaceCheck.TemporarySpaceNeeded)}");
        sb.AppendLine($"  Total: ~{FormatBytes(spaceCheck.TotalNeeded)}\n");
        sb.AppendLine($"Space available: {FormatBytes(spaceCheck.AvailableSpace)}");
        if (spaceCheck.SpaceToBeFreed > 0)
        {
            sb.AppendLine($"Space to be freed: {FormatBytes(spaceCheck.SpaceToBeFreed)}");
            sb.AppendLine($"Effective available: {FormatBytes(spaceCheck.EffectiveAvailable)}");
        }
        if (spaceCheck.Shortfall > 0)
            sb.AppendLine($"\nSD card shortfall: ~{FormatBytes(spaceCheck.Shortfall)}");
        if (spaceCheck.ExternalTemporarySpaceNeeded > 0)
        {
            sb.AppendLine($"\nTemporary folder: {spaceCheck.TemporaryFolderPath}");
            sb.AppendLine($"Temporary space needed: ~{FormatBytes(spaceCheck.ExternalTemporarySpaceNeeded)}");
            sb.AppendLine($"Temporary space available: {FormatBytes(spaceCheck.TemporaryAvailableSpace)}");
            if (spaceCheck.TemporaryShortfall > 0)
                sb.AppendLine($"Temporary space shortfall: ~{FormatBytes(spaceCheck.TemporaryShortfall)}");
        }
        if (spaceCheck.ContainsEstimatedSizes)
            sb.AppendLine("\nNote: Some items are compressed or need conversion and their final sizes are estimates.");
        sb.Append("\nDo you want to proceed anyway?");
        return sb.ToString();
    }

    private static string FormatBytes(long bytes) => ByteSize.FromBytes(bytes).ToString("0.##");

    private static (string Volume, long Available) GetVolumeSpace(string path)
    {
        string fullPath = Path.GetFullPath(path);
        try
        {
            string resolved = Path.GetPathRoot(fullPath)!;
            foreach (string part in Path.GetRelativePath(resolved, fullPath).Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                var directory = new DirectoryInfo(Path.Combine(resolved, part));
                resolved = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
            }
            if (OperatingSystem.IsWindows())
            {
                var mount = new StringBuilder(1024);
                var volume = new StringBuilder(1024);
                if (!GetVolumePathName(resolved, mount, mount.Capacity) ||
                    !GetDiskFreeSpaceEx(resolved, out ulong available, out _, out _))
                    return (fullPath, 0);
                string identity = GetVolumeNameForVolumeMountPoint(mount.ToString(), volume, volume.Capacity)
                    ? volume.ToString() : mount.ToString();
                return (identity, (long)Math.Min(available, (ulong)long.MaxValue));
            }
            string directoryPath = resolved.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var drive = DriveInfo.GetDrives().Where(d => d.IsReady)
                .Where(d => directoryPath.StartsWith(d.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
            return drive == null ? (fullPath, 0) : (drive.RootDirectory.FullName, drive.AvailableFreeSpace);
        }
        catch (IOException) { return (fullPath, 0); }
        catch (UnauthorizedAccessException) { return (fullPath, 0); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, StringBuilder volumeName, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong availableBytes, out ulong totalBytes, out ulong totalFreeBytes);

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

    private TownsGame? LoadGameFromSource(string sourcePath, string? specificFile)
    {
        if (specificFile == null)
        {
            var files = Directory.GetFiles(sourcePath);
            specificFile =
                files.FirstOrDefault(f => Path.GetExtension(f).Equals(".ccd", StringComparison.OrdinalIgnoreCase)) ??
                files.FirstOrDefault(f => Path.GetExtension(f).Equals(".cue", StringComparison.OrdinalIgnoreCase)) ??
                files.FirstOrDefault(f => Path.GetExtension(f).Equals(".chd", StringComparison.OrdinalIgnoreCase)) ??
                files.FirstOrDefault(f => Path.GetExtension(f).Equals(".mds", StringComparison.OrdinalIgnoreCase)) ??
                files.FirstOrDefault(f => Constants.AllImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            if (specificFile == null) return null;
        }

        // If a specific CUE file was selected, parse it to find related BIN files.
        bool isCueFile = Path.GetExtension(specificFile).Equals(".cue", StringComparison.OrdinalIgnoreCase);

        // If a specific CCD file was selected, find its companion .img and .sub files.
        bool isCcdFile = Path.GetExtension(specificFile).Equals(".ccd", StringComparison.OrdinalIgnoreCase);

        // If a specific MDS file was selected, find its companion .mdf file.
        bool isMdsFile = Path.GetExtension(specificFile).Equals(".mds", StringComparison.OrdinalIgnoreCase);

        List<string>? cueRelatedFiles = null;
        if (isCueFile)
        {
            cueRelatedFiles = CueSheetParser.GetAllRelatedFiles(specificFile!);
        }

        List<string>? companionFiles = null;
        if (isCcdFile || isMdsFile)
        {
            companionFiles = new List<string> { specificFile! };
            var siblings = Directory.GetFiles(Path.GetDirectoryName(specificFile)!);
            foreach (string extension in isCcdFile ? new[] { ".img", ".sub" } : new[] { ".mdf" })
            {
                string name = Path.GetFileNameWithoutExtension(specificFile) + extension;
                string? companion = siblings.FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
                if (companion != null) companionFiles.Add(companion);
            }
        }

        string displayName = Path.GetFileNameWithoutExtension(specificFile);

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
        else
        {
            // Specific non-CUE/non-CCD file selected.
            game.ImageFiles.Add(specificFile);
            totalSize += new FileInfo(specificFile).Length;

            if (Path.GetExtension(specificFile).Equals(".chd", StringComparison.OrdinalIgnoreCase))
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
        NotifyObservers(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
    }
}
