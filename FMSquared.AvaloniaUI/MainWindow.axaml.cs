using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using MsBoxIcon = MsBox.Avalonia.Enums.Icon;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using FMSquared.Core;
using FMSquared.Core.Models;
using FMSquared.Core.Services;

namespace FMSquared;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private DataGrid GameGrid = null!;
    private Border DropLine = null!;
    private TextBox FilterTextBox = null!;
    private ComboBox DriveComboBox = null!;
    private TextBox TempFolderTextBox = null!;
    private CheckBox LockCheckBox = null!;
    private RadioButton RadioAlmanac = null!;
    private RadioButton RadioSpellbook = null!;

    private readonly Manager _manager = new();
    private readonly AppSettings _settings;
    private bool _suppressMenuTypeChange;

    // Root path of each drive in DriveComboBox, by index. The combo items are
    // display labels and the path can't be recovered from them.
    private readonly List<string> _drivePaths = new();

    // Where a drop will land, worked out while the drag hovers and reused when it
    // lands. -1 means no spot has been settled on yet.
    private int _pendingDropIndex = -1;

    // Row reorder drag state. The dragged games ride in _rowDragItems since the drag
    // starts and ends in this window. The marker format exists because macOS refuses
    // a drag that declares no pasteboard types at all.
    private static readonly DataFormat<byte[]> RowDragFormat =
        DataFormat.CreateBytesApplicationFormat("fm2-games-row-drag");
    private PointerPressedEventArgs? _rowDragTrigger;
    private Point _rowDragStartPoint;
    private TownsGame? _rowDragPressedItem;
    private List<TownsGame>? _rowDragItems;

    // Optional stdout logging for drag and drop, enabled with FM2_DND_DEBUG=1.
    private static readonly bool DndDebug =
        Environment.GetEnvironmentVariable("FM2_DND_DEBUG") == "1";

    private static void DndLog(string message)
    {
        if (DndDebug)
            Console.WriteLine($"[dnd] {message}");
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; RaisePropertyChanged(); }
    }

    private bool _isUsingCustomPath;
    public bool IsUsingCustomPath
    {
        get => _isUsingCustomPath;
        set { _isUsingCustomPath = value; RaisePropertyChanged(); }
    }

    private string _customSdPath = string.Empty;
    public string CustomSdPath
    {
        get => _customSdPath;
        set { _customSdPath = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(IsUsingCustomPath)); RaisePropertyChanged(nameof(HasSdPath)); RaisePropertyChanged(nameof(CanModifyList)); }
    }

    public bool HasSdPath => !string.IsNullOrEmpty(_manager.SdCardPath);

    private bool _isFilterActive;
    public bool IsFilterActive
    {
        get => _isFilterActive;
        set { _isFilterActive = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(CanModifyList)); }
    }

    public bool CanModifyList => HasSdPath && !IsFilterActive;

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set { _searchText = value ?? string.Empty; RaisePropertyChanged(); UpdateSearchMatches(); }
    }

    public UndoManager UndoManager => _manager.UndoManager;

    private string _gamesListHeader = "N/A";
    public string GamesListHeader
    {
        get => _gamesListHeader;
        private set { _gamesListHeader = value; RaisePropertyChanged(); }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        Title = "FM^2 v" + Constants.Version;
        DataContext = this;

        _settings = AppSettings.Load();
        ApplySettings();

        UpdateManager.CleanupStaleStagingData();

        GameGrid.ItemsSource = _manager.ItemList;

        _manager.ItemList.CollectionChanged += (_, _) => { UpdateGamesListHeader(); UpdateSearchMatches(); };

        FilterTextBox.KeyDown += FilterTextBox_KeyDown;
        AddHandler(DragDrop.DropEvent, WindowDrop);
        AddHandler(DragDrop.DragOverEvent, WindowDragOver);
        AddHandler(DragDrop.DragLeaveEvent, WindowDragLeave);
        GameGrid.AddHandler(InputElement.PointerPressedEvent, DataGrid_PointerPressed, RoutingStrategies.Tunnel);
        GameGrid.AddHandler(InputElement.PointerReleasedEvent, DataGrid_PointerReleased, RoutingStrategies.Tunnel);
        GameGrid.PointerMoved += DataGrid_PointerMoved;
        KeyDown += MainWindow_KeyDown;
        Closing += MainWindow_Closing;
        Opened += MainWindow_Opened;

        _manager.OnFolderLocked = async (path) =>
        {
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Confirmation",
                $"The following folder is open in another program:\n\n{path}\n\n" +
                "Close any programs using it, then click Yes to retry.",
                ButtonEnum.YesNo, MsBoxIcon.Warning);
            var result = await msgBox.ShowWindowDialogAsync(this);
            return result == ButtonResult.Yes;
        };

        _manager.OnArchiveWarning = async (message) =>
        {
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Warning", message, ButtonEnum.Ok, MsBoxIcon.Warning);
            await msgBox.ShowWindowDialogAsync(this);
        };

        RefreshDriveList();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        GameGrid = this.FindControl<DataGrid>("GameGrid")!;
        DropLine = this.FindControl<Border>("DropLine")!;
        FilterTextBox = this.FindControl<TextBox>("FilterTextBox")!;
        DriveComboBox = this.FindControl<ComboBox>("DriveComboBox")!;
        TempFolderTextBox = this.FindControl<TextBox>("TempFolderTextBox")!;
        LockCheckBox = this.FindControl<CheckBox>("LockCheckBox")!;
        RadioAlmanac = this.FindControl<RadioButton>("RadioAlmanac")!;
        RadioSpellbook = this.FindControl<RadioButton>("RadioSpellbook")!;
    }

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        try
        {
            var readOnlyPath = AppSettings.CheckReadOnly();
            if (readOnlyPath != null)
            {
                var msgBox = MessageBoxManager.GetMessageBoxStandard(
                    "Information",
                    $"The settings file is marked as read-only:\n\n{readOnlyPath}\n\n" +
                    "Your preferences will not be saved until this is resolved.",
                    ButtonEnum.Ok, MsBoxIcon.Warning);
                await msgBox.ShowWindowDialogAsync(this);
            }
        }
        catch { }

        _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var result = await UpdateManager.CheckForUpdateAsync();
            if (result.ManualUpdateRequired && !UpdateAvailableDialog.ShouldSkipVersion(result.LatestTag))
            {
                var manualDialog = new ManualUpdateDialog(result.LatestTag, result.LatestVersion, result.ManualReason);
                await manualDialog.ShowDialog(this);
            }
            else if (result.UpdateAvailable && !UpdateAvailableDialog.ShouldSkipVersion(result.LatestTag))
            {
                var dialog = new UpdateAvailableDialog(result.LatestTag, result.LatestVersion);
                await dialog.ShowDialog(this);

                if (dialog.UserWantsUpdate)
                {
                    var wizard = new UpdateWizardWindow(result.LatestTag, result.LatestVersion);
                    await wizard.ShowDialog(this);
                }
            }
        }
        catch { }
    }

    private void ApplySettings()
    {
        LockCheckBox.IsChecked = _settings.EnableLockCheck;

        if (!string.IsNullOrEmpty(_settings.TempFolder) && Directory.Exists(_settings.TempFolder))
            TempFolderTextBox.Text = _settings.TempFolder;
        else
            TempFolderTextBox.Text = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new Avalonia.PixelPoint((int)_settings.WindowLeft, (int)_settings.WindowTop);
        }

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
    }

    private void SaveSettings()
    {
        _settings.EnableLockCheck = LockCheckBox.IsChecked == true;
        _settings.TempFolder = NormalizeTempFolderForSave(TempFolderTextBox.Text);
        _settings.WindowLeft = Position.X;
        _settings.WindowTop = Position.Y;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _settings.Save();
    }

    private static string NormalizeTempFolderForSave(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        string normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string systemDefault = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalized, systemDefault, StringComparison.OrdinalIgnoreCase))
            return "";
        return normalized;
    }

    private string GetTempFolderRoot()
    {
        string text = TempFolderTextBox.Text ?? "";
        return !string.IsNullOrEmpty(text) && Directory.Exists(text) ? text : "";
    }

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (IsBusy)
        {
            e.Cancel = true;
            return;
        }
        SaveSettings();
    }

    private void UpdateGamesListHeader()
    {
        long totalBytes = _manager.ItemList
            .Where(g => !g.IsMenuItem && g.Length > 0)
            .Sum(g => g.Length);

        if (totalBytes > 0)
        {
            double gb = totalBytes / 1_000_000_000.0;
            GamesListHeader = $"{gb:F2} GB";
        }
        else
        {
            GamesListHeader = "N/A";
        }
    }

    // --- Drive selection ---

    private void RefreshDriveList()
    {
        IsUsingCustomPath = false;
        CustomSdPath = string.Empty;
        DriveComboBox.Items.Clear();
        _drivePaths.Clear();

        int autoSelectIndex = -1;
        int index = 0;

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Removable || drive.DriveType == DriveType.Fixed)
            {
                string label = drive.IsReady && !string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? $"{drive.Name} ({drive.VolumeLabel})"
                    : drive.Name;
                DriveComboBox.Items.Add(label);
                _drivePaths.Add(drive.Name);

                if (autoSelectIndex == -1 && drive.IsReady)
                {
                    try
                    {
                        if (HasOdeIni(drive.RootDirectory.FullName))
                            autoSelectIndex = index;
                    }
                    catch { }
                }

                index++;
            }
        }

        if (autoSelectIndex >= 0)
            DriveComboBox.SelectedIndex = autoSelectIndex;
    }

    private static bool HasOdeIni(string root)
    {
        try
        {
            var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
            foreach (var file in Directory.GetFiles(root, "*.ini", options))
            {
                string name = Path.GetFileName(file);
                if (name.Equals(Constants.DocBrownIniFile, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(Constants.WizardIniFile, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private void ButtonRefreshDrives_Click(object? sender, RoutedEventArgs e) => RefreshDriveList();

    private async void ButtonBrowseSdPath_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select SD card or folder",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;

        string folderPath = folders[0].Path.LocalPath;

        IsUsingCustomPath = true;
        CustomSdPath = folderPath;
        DriveComboBox.SelectedIndex = -1;

        _manager.ToolsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
        _manager.SdCardPath = folderPath;

        RaisePropertyChanged(nameof(HasSdPath));
        RaisePropertyChanged(nameof(CanModifyList));
        await LoadCard();
    }

    private async void ButtonBrowseTempFolder_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var options = new FolderPickerOpenOptions
        {
            Title = "Select temporary folder",
            AllowMultiple = false
        };

        string currentPath = TempFolderTextBox.Text ?? "";
        if (!string.IsNullOrEmpty(currentPath) && Directory.Exists(currentPath))
        {
            try
            {
                options.SuggestedStartLocation = await topLevel.StorageProvider
                    .TryGetFolderFromPathAsync(new Uri("file:///" + currentPath.Replace('\\', '/')));
            }
            catch { }
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        if (folders.Count == 0) return;

        TempFolderTextBox.Text = folders[0].Path.LocalPath;
        SaveSettings();
    }

    private async void ButtonResetTempFolder_Click(object? sender, RoutedEventArgs e)
    {
        var msgBox = MessageBoxManager.GetMessageBoxStandard(
            "Confirmation",
            "Reset the Temporary Folder path to default?",
            ButtonEnum.YesNo, MsBoxIcon.Question);
        var result = await msgBox.ShowWindowDialogAsync(this);
        if (result != ButtonResult.Yes) return;

        TempFolderTextBox.Text = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        SaveSettings();
    }

    private async void DriveList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int selectedIndex = DriveComboBox.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= _drivePaths.Count || IsBusy) return;

        string drivePath = _drivePaths[selectedIndex];

        IsUsingCustomPath = false;
        CustomSdPath = string.Empty;

        _manager.ToolsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
        _manager.SdCardPath = drivePath;

        RaisePropertyChanged(nameof(HasSdPath));
        RaisePropertyChanged(nameof(CanModifyList));
        await LoadCard();
    }

    private async Task LoadCard()
    {
        IsBusy = true;
        FilterTextBox.Text = string.Empty;
        IsFilterActive = false;
        GameGrid.ItemsSource = _manager.ItemList;

        try
        {
            await _manager.LoadItemsFromCardAsync();

            UpdateGamesListHeader();
            UpdateOdeDisplay();
        }
        catch (Exception ex)
        {
            var msgBox = MessageBoxManager.GetMessageBoxStandard("Error", ex.Message, ButtonEnum.Ok, MsBoxIcon.Error);
            await msgBox.ShowWindowDialogAsync(this);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateOdeDisplay()
    {
        _suppressMenuTypeChange = true;
        try
        {
            if (_manager.OdeKindSelected == OdeKind.Wizard)
                RadioSpellbook.IsChecked = true;
            else
                RadioAlmanac.IsChecked = true;
        }
        finally
        {
            _suppressMenuTypeChange = false;
        }
    }

    // --- Menu type ---

    private void MenuType_Changed(object? sender, RoutedEventArgs e)
    {
        // The default radio raises the change while the window is still being
        // parsed, before the other radio exists.
        if (_suppressMenuTypeChange || RadioSpellbook == null) return;

        _manager.OdeKindSelected = RadioSpellbook.IsChecked == true
            ? OdeKind.Wizard
            : OdeKind.DocBrown;
    }

    // --- Game list operations ---

    private async void ButtonAdd_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select disc image file(s)",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("FM Towns Disc Images")
                {
                    Patterns = new[] { "*.cdi", "*.mdf", "*.mds", "*.img", "*.bin", "*.iso", "*.ccd", "*.cue", "*.chd", "*.7z", "*.rar", "*.zip" }
                },
                new FilePickerFileType("All Files")
                {
                    Patterns = new[] { "*.*" }
                }
            }
        });

        if (files.Count == 0) return;

        var paths = files.Select(f => f.Path.LocalPath).ToArray();
        await AddGamesFromPaths(paths);
    }

    private async Task AddGamesFromPaths(string[] paths, int insertIndex = -1)
    {
        IsBusy = true;

        ProgressWindow? progressWindow = null;
        if (paths.Length > 1)
        {
            progressWindow = new ProgressWindow();
            progressWindow.Title = "Adding Disc Images";
            progressWindow.IsIndeterminate = true;
        }

        try
        {
            // Shown on the first report, so a fast add never flashes it.
            var progress = new Progress<string>(msg =>
            {
                if (progressWindow != null)
                {
                    if (!progressWindow.IsVisible)
                        progressWindow.Show(this);
                    progressWindow.TextContent = msg;
                }
            });

            await _manager.AddGamesAsync(paths, progress, insertIndex);
        }
        catch (Exception ex)
        {
            var msgBox = MessageBoxManager.GetMessageBoxStandard("Error", ex.Message, ButtonEnum.Ok, MsBoxIcon.Error);
            await msgBox.ShowWindowDialogAsync(this);
        }
        finally
        {
            if (progressWindow != null)
            {
                progressWindow.AllowClose();
                progressWindow.Close();
            }
            IsBusy = false;
        }
    }

    private void ButtonRemove_Click(object? sender, RoutedEventArgs e)
    {
        var selected = GameGrid.SelectedItems?.Cast<TownsGame>().ToList();
        if (selected == null || selected.Count == 0) return;

        _manager.RemoveItems(selected);
    }

    private async void ButtonFloppyBoot_Click(object? sender, RoutedEventArgs e)
    {
        if (_manager.HasFloppyBootEntry)
        {
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Information", "The list already contains a floppy boot entry.",
                ButtonEnum.Ok, MsBoxIcon.Info);
            await msgBox.ShowWindowDialogAsync(this);
            return;
        }

        var dialog = new FloppyBootWindow();
        await dialog.ShowDialog(this);

        if (dialog.UserConfirmed)
            _manager.InsertFloppyBootEntry();
    }

    private void ButtonMoveUp_Click(object? sender, RoutedEventArgs e)
    {
        if (GameGrid.SelectedItem is not TownsGame item) return;
        if (item.IsMenuItem) return;
        int index = _manager.ItemList.IndexOf(item);
        if (index <= 0) return;

        var above = _manager.ItemList[index - 1];
        if (above.IsMenuItem) return;

        var oldOrder = _manager.ItemList.ToList();
        _manager.ItemList.Move(index, index - 1);
        _manager.UndoManager.RecordChange(new ListReorderOperation("Move Up")
        {
            ItemList = _manager.ItemList,
            OldOrder = oldOrder,
            NewOrder = _manager.ItemList.ToList()
        });
    }

    private void ButtonMoveDown_Click(object? sender, RoutedEventArgs e)
    {
        if (GameGrid.SelectedItem is not TownsGame item) return;
        if (item.IsMenuItem) return;

        int index = _manager.ItemList.IndexOf(item);
        if (index < 0 || index >= _manager.ItemList.Count - 1) return;

        var oldOrder = _manager.ItemList.ToList();
        _manager.ItemList.Move(index, index + 1);
        _manager.UndoManager.RecordChange(new ListReorderOperation("Move Down")
        {
            ItemList = _manager.ItemList,
            OldOrder = oldOrder,
            NewOrder = _manager.ItemList.ToList()
        });
    }

    private async void ButtonSort_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var msgBox = MessageBoxManager.GetMessageBoxStandard(
                "Confirmation",
                "Your disc images will be sorted alphabetically by title.\n\nProceed?",
                ButtonEnum.YesNo, MsBoxIcon.Question);

            var result = await msgBox.ShowWindowDialogAsync(this);
            if (result != ButtonResult.Yes) return;

            _manager.SortList();
        }
        catch { }
    }

    // --- Search/Filter ---

    private void FilterTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ButtonSearch_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void ButtonSearch_Click(object? sender, RoutedEventArgs e)
    {
        string filterText = FilterTextBox.Text?.Trim() ?? string.Empty;
        if (_manager.ItemList.Count == 0 || string.IsNullOrWhiteSpace(filterText))
            return;

        int startIndex = GameGrid.SelectedIndex == -1 ? 0 : GameGrid.SelectedIndex;

        if (!SearchInGrid(startIndex, filterText))
        {
            if (!SearchInGrid(0, filterText))
            {
                var msgBox = MessageBoxManager.GetMessageBoxStandard(
                    "Information", "No matches found.", ButtonEnum.Ok, MsBoxIcon.Info);
                await msgBox.ShowWindowDialogAsync(this);
            }
        }
    }

    private bool SearchInGrid(int start, string filter)
    {
        var items = GameGrid.ItemsSource?.Cast<TownsGame>().ToList();
        if (items == null) return false;

        for (int i = start; i < items.Count; i++)
        {
            var item = items[i];
            if (GameGrid.SelectedItem != item && _manager.SearchInItem(item, filter))
            {
                GameGrid.SelectedItem = item;
                GameGrid.ScrollIntoView(item, null);
                return true;
            }
        }

        return false;
    }

    private void ButtonFilter_Click(object? sender, RoutedEventArgs e)
    {
        string filterText = FilterTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(filterText))
            return;

        var filtered = _manager.ItemList
            .Where(item => item.IsMenuItem ||
                (item.Name?.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0))
            .ToList();

        GameGrid.ItemsSource = filtered;
        IsFilterActive = true;
    }

    private void ButtonFilterReset_Click(object? sender, RoutedEventArgs e)
    {
        GameGrid.ItemsSource = _manager.ItemList;
        FilterTextBox.Text = string.Empty;
        IsFilterActive = false;
    }

    private void UpdateSearchMatches()
    {
        var text = _searchText.Trim();
        foreach (var item in _manager.ItemList)
            item.IsMatch = text.Length > 0 && _manager.SearchInItem(item, text);
    }

    // --- Save ---

    private async void ButtonSave_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_manager.SdCardPath))
        {
            var msgBox = MessageBoxManager.GetMessageBoxStandard("Error", "No SD card selected.", ButtonEnum.Ok, MsBoxIcon.Warning);
            await msgBox.ShowWindowDialogAsync(this);
            return;
        }

        var confirmBox = MessageBoxManager.GetMessageBoxStandard(
            "Confirmation",
            $"Save changes to \"{_manager.SdCardPath}\" drive?\n\n" +
            "Game folders will be renumbered to match the list order.",
            ButtonEnum.YesNo, MsBoxIcon.Question);

        var confirmResult = await confirmBox.ShowWindowDialogAsync(this);
        if (confirmResult != ButtonResult.Yes) return;

        var spaceCheck = await _manager.CalculateRequiredSpaceAsync();
        if (!spaceCheck.HasSufficientSpace)
        {
            var spaceBox = MessageBoxManager.GetMessageBoxStandard(
                "Confirmation",
                Manager.BuildSpaceWarningMessage(spaceCheck),
                ButtonEnum.YesNo, MsBoxIcon.Warning);
            if (await spaceBox.ShowWindowDialogAsync(this) != ButtonResult.Yes) return;
        }

        IsBusy = true;

        try
        {
            if (LockCheckBox.IsChecked == true)
            {
                bool lockCheckPassed = await RunLockCheck();
                if (!lockCheckPassed)
                    return;
            }

            var progressWindow = new ProgressWindow();
            progressWindow.TotalItems = _manager.ItemList.Count(g => !g.IsMenuItem);
            progressWindow.IsIndeterminate = true;
            progressWindow.Show(this);

            try
            {
                var progress = new Progress<string>(msg =>
                {
                    progressWindow.TextContent = msg;
                });

                var itemProgress = new Progress<int>(count =>
                {
                    progressWindow.ProcessedItems = count;
                });

                string tempRoot = GetTempFolderRoot();
                await _manager.SaveAsync(progress, itemProgress, string.IsNullOrEmpty(tempRoot) ? null : tempRoot);

                SaveSettings();

                progressWindow.AllowClose();
                progressWindow.Close();

                var doneBox = MessageBoxManager.GetMessageBoxStandard("Information", "Done!", ButtonEnum.Ok, MsBoxIcon.Info);
                await doneBox.ShowWindowDialogAsync(this);

                await LoadCard();
            }
            finally
            {
                progressWindow.AllowClose();
                if (progressWindow.IsVisible)
                    progressWindow.Close();
            }
        }
        catch (Exception ex)
        {
            var msgBox = MessageBoxManager.GetMessageBoxStandard("Error", ex.Message, ButtonEnum.Ok, MsBoxIcon.Error);
            await msgBox.ShowWindowDialogAsync(this);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> RunLockCheck()
    {
        while (true)
        {
            var paths = _manager.CollectPathsToModify();

            var lockProgress = new ProgressWindow();
            lockProgress.TextContent = "Checking for locked files and folders...";
            lockProgress.TotalItems = paths.Count;
            lockProgress.Show(this);

            Dictionary<string, string> locked;
            try
            {
                var progress = new Progress<(int current, int total, string name)>(info =>
                {
                    lockProgress.ProcessedItems = info.current;
                });

                locked = await LockChecker.CheckPathsAsync(paths, progress);
            }
            finally
            {
                lockProgress.AllowClose();
                lockProgress.Close();
            }

            if (locked.Count == 0)
                return true;

            var dialog = new LockedFilesDialog(locked);
            await dialog.ShowDialog(this);

            if (!dialog.UserWantsRetry)
                return false;
        }
    }

    // --- Context menu ---

    private void MenuItemRename_Click(object? sender, RoutedEventArgs e)
    {
        if (GameGrid.SelectedItem != null)
            GameGrid.BeginEdit();
    }

    private void MenuItemTitleCase_Click(object? sender, RoutedEventArgs e)
    {
        RenameSelectedItems(name => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()));
    }

    private void MenuItemUppercase_Click(object? sender, RoutedEventArgs e)
    {
        RenameSelectedItems(name => name.ToUpperInvariant());
    }

    private void MenuItemLowercase_Click(object? sender, RoutedEventArgs e)
    {
        RenameSelectedItems(name => name.ToLowerInvariant());
    }

    private void RenameSelectedItems(Func<string, string> transform)
    {
        var items = GameGrid.SelectedItems?.Cast<TownsGame>()
            .Where(g => !g.IsMenuItem)
            .ToList();
        if (items == null || items.Count == 0) return;

        var undoOp = new MultiPropertyEditOperation("Rename")
        {
            PropertyName = nameof(TownsGame.Name)
        };

        foreach (var game in items)
        {
            var oldName = game.Name;
            game.Name = transform(oldName);
            if (oldName != game.Name)
            {
                undoOp.AddChange(game, oldName, game.Name);
                game.TitleDirty = true;
            }
        }

        if (undoOp.HasChanges)
            _manager.UndoManager.RecordChange(undoOp);
    }

    private void MenuItemRenameFolder_Click(object? sender, RoutedEventArgs e)
    {
        var items = GameGrid.SelectedItems?.Cast<TownsGame>()
            .Where(g => !g.IsMenuItem && g.IsNotOnSdCard)
            .ToList();
        if (items == null || items.Count == 0) return;

        var undoOp = new MultiPropertyEditOperation("Rename by Folder")
        {
            PropertyName = nameof(TownsGame.Name)
        };

        foreach (var game in items)
        {
            string? folderPath = !string.IsNullOrEmpty(game.SourcePath) ? game.SourcePath : null;
            if (string.IsNullOrEmpty(folderPath)) continue;

            var oldName = game.Name;
            string rawName = game.FileFormat == FileFormat.Compressed
                ? Path.GetFileNameWithoutExtension(folderPath)
                : Path.GetFileName(folderPath);
            game.Name = NameSanitizer.Sanitize(rawName);
            if (oldName != game.Name)
            {
                undoOp.AddChange(game, oldName, game.Name);
                game.TitleDirty = true;
            }
        }

        if (undoOp.HasChanges)
            _manager.UndoManager.RecordChange(undoOp);
    }

    private void MenuItemRenameFile_Click(object? sender, RoutedEventArgs e)
    {
        var items = GameGrid.SelectedItems?.Cast<TownsGame>()
            .Where(g => !g.IsMenuItem)
            .ToList();
        if (items == null || items.Count == 0) return;

        var undoOp = new MultiPropertyEditOperation("Rename by File")
        {
            PropertyName = nameof(TownsGame.Name)
        };

        foreach (var game in items)
        {
            if (game.ImageFiles.Count == 0) continue;

            var oldName = game.Name;
            game.Name = NameSanitizer.Sanitize(Path.GetFileNameWithoutExtension(game.ImageFiles[0]));
            if (oldName != game.Name)
            {
                undoOp.AddChange(game, oldName, game.Name);
                game.TitleDirty = true;
            }
        }

        if (undoOp.HasChanges)
            _manager.UndoManager.RecordChange(undoOp);
    }

    private void ContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        // Block context menu on menu item rows.
        if (GameGrid.SelectedItem is TownsGame selected && selected.IsMenuItem)
        {
            e.Cancel = true;
            return;
        }

        int count = GameGrid.SelectedItems?.Cast<TownsGame>()
            .Count(g => !g.IsMenuItem) ?? 0;
        bool isMultiple = count > 1;

        var renameItem = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(m => m.Name == "MenuItemRename");
        if (renameItem != null)
            renameItem.IsEnabled = !isMultiple;

        var autoRenameItem = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(m => m.Name == "MenuItemAutoRename");
        if (autoRenameItem != null)
        {
            autoRenameItem.Header = isMultiple ? "Automatically Rename Titles" : "Automatically Rename Title";

            // Folder rename needs a source folder on the computer, so it is only
            // available when ALL selected items are off the SD card. File rename
            // works for on-card games too since their disc images are known.
            bool allOffSdCard = GameGrid.SelectedItems?.Cast<TownsGame>()
                .Where(g => !g.IsMenuItem)
                .All(g => g.IsNotOnSdCard) ?? false;

            var renameFolderItem = autoRenameItem.Items.OfType<MenuItem>()
                .FirstOrDefault(m => m.Name == "MenuItemRenameFolder");
            if (renameFolderItem != null)
                renameFolderItem.IsEnabled = allOffSdCard;

            var renameFileItem = autoRenameItem.Items.OfType<MenuItem>()
                .FirstOrDefault(m => m.Name == "MenuItemRenameFile");
            if (renameFileItem != null)
                renameFileItem.IsEnabled = true;
        }
    }

    // --- Drag and drop ---

    private void DataGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(GameGrid).Properties.IsLeftButtonPressed)
            return;

        // A press inside a cell editor is for editing text, not for dragging.
        if (e.Source is TextBox)
            return;

        // A left press on a row may turn into a reorder drag. Remember it until the
        // pointer has moved far enough to count as one.
        var pressSource = e.Source as Control;
        while (pressSource != null)
        {
            if (pressSource is DataGridRow pressRow)
            {
                _rowDragPressedItem = pressRow.DataContext as TownsGame;
                _rowDragTrigger = _rowDragPressedItem != null ? e : null;
                _rowDragStartPoint = e.GetPosition(this);
                return;
            }
            pressSource = pressSource.Parent as Control;
        }
    }

    private void DataGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Released without crossing the drag threshold, so this was a plain click.
        _rowDragTrigger = null;
        _rowDragPressedItem = null;
    }

    private async void DataGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_rowDragTrigger == null || _rowDragItems != null)
            return;

        if (!e.GetCurrentPoint(GameGrid).Properties.IsLeftButtonPressed)
        {
            _rowDragTrigger = null;
            _rowDragPressedItem = null;
            return;
        }

        if (IsBusy || IsFilterActive || !HasSdPath || _editOldValue != null)
            return;

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _rowDragStartPoint.X) < 4 &&
            Math.Abs(current.Y - _rowDragStartPoint.Y) < 4)
            return;

        // Dragging a row that is part of the selection moves the whole selection.
        var items = new List<TownsGame>();
        if (_rowDragPressedItem != null)
        {
            if (GameGrid.SelectedItems != null && GameGrid.SelectedItems.Contains(_rowDragPressedItem) &&
                GameGrid.SelectedItems.Count > 1)
                items.AddRange(GameGrid.SelectedItems.OfType<TownsGame>().OrderBy(g => _manager.ItemList.IndexOf(g)));
            else
                items.Add(_rowDragPressedItem);
        }

        // The menu entry stays at the top of the list and never gets dragged.
        if (items.Count == 0 || items.Any(g => g.IsMenuItem))
        {
            _rowDragTrigger = null;
            _rowDragPressedItem = null;
            return;
        }

        var trigger = _rowDragTrigger;
        _rowDragTrigger = null;
        _rowDragPressedItem = null;
        _rowDragItems = items;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(RowDragFormat, new byte[] { 1 }));

        try
        {
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
        }
        catch (Exception)
        {
            // A failed platform drag just cancels the move.
        }

        _rowDragItems = null;
        _pendingDropIndex = -1;
        HideDropLine();
    }

    private async void WindowDrop(object? sender, DragEventArgs e)
    {
        HideDropLine();

        int pending = _pendingDropIndex;
        _pendingDropIndex = -1;

        if (IsFilterActive || !HasSdPath)
            return;

        if (_rowDragItems != null && e.DataTransfer.Contains(RowDragFormat))
        {
            try
            {
                // Reorder drop. Remove the dragged rows, walking the target index
                // back for each one that sat above it, then put them back at the
                // target spot.
                int moveIndex = pending >= 0 ? pending : DefaultDropIndex();
                moveIndex = Math.Min(moveIndex, _manager.ItemList.Count);

                var oldOrder = _manager.ItemList.ToList();

                foreach (var item in _rowDragItems)
                {
                    int idx = _manager.ItemList.IndexOf(item);
                    if (idx < 0)
                        continue;
                    _manager.ItemList.RemoveAt(idx);
                    if (idx < moveIndex)
                        moveIndex--;
                }

                if (moveIndex == 0 && _manager.ItemList.Count > 0 && _manager.ItemList[0].IsMenuItem)
                    moveIndex = 1;
                moveIndex = Math.Min(moveIndex, _manager.ItemList.Count);

                DndLog($"reorder drop at index {moveIndex}");

                foreach (var item in _rowDragItems)
                    _manager.ItemList.Insert(moveIndex++, item);

                if (!oldOrder.SequenceEqual(_manager.ItemList))
                {
                    _manager.UndoManager.RecordChange(new ListReorderOperation("Reorder List")
                    {
                        ItemList = _manager.ItemList,
                        OldOrder = oldOrder,
                        NewOrder = _manager.ItemList.ToList()
                    });
                }
            }
            catch (Exception ex)
            {
                var msgBox = MessageBoxManager.GetMessageBoxStandard("Error", ex.Message, ButtonEnum.Ok, MsBoxIcon.Error);
                await msgBox.ShowWindowDialogAsync(this);
            }
            return;
        }

        if (e.DataTransfer.Contains(DataFormat.File))
        {
            // Land the drop where the guide line settled during the drag. The drop
            // event position is not reliable on some Linux setups, the hover
            // position tracked in WindowDragOver is.
            int insertIndex = pending >= 0 ? pending : DefaultDropIndex();
            insertIndex = Math.Min(insertIndex, _manager.ItemList.Count);

            var droppedItems = e.DataTransfer.TryGetFiles() ?? Array.Empty<IStorageItem>();
            var paths = new List<string>();
            var invalid = new List<string>();

            foreach (var storageItem in droppedItems)
            {
                var path = storageItem.TryGetLocalPath();
                if (path == null)
                    invalid.Add($"{storageItem.Name} is not a local file.");
                else
                    paths.Add(path);
            }

            DndLog($"file drop of {paths.Count} item(s) at index {insertIndex}");

            if (paths.Count > 0)
                await AddGamesFromPaths(paths.ToArray(), insertIndex);

            if (invalid.Count > 0)
            {
                var msgBox = MessageBoxManager.GetMessageBoxStandard("Error", string.Join(Environment.NewLine, invalid), ButtonEnum.Ok, MsBoxIcon.Error);
                await msgBox.ShowWindowDialogAsync(this);
            }
        }
    }

    private void WindowDragOver(object? sender, DragEventArgs e)
    {
        bool isFileDrag = e.DataTransfer.Contains(DataFormat.File);
        bool isRowDrag = _rowDragItems != null && e.DataTransfer.Contains(RowDragFormat);

        if (IsFilterActive || !HasSdPath || (!isFileDrag && !isRowDrag))
        {
            _pendingDropIndex = -1;
            HideDropLine();
            return;
        }

        if (isRowDrag)
            e.DragEffects = DragDropEffects.Move;

        var target = HitTestDropRow(e);
        if (target == null)
        {
            _pendingDropIndex = DefaultDropIndex();
            HideDropLine();
        }
        else
        {
            _pendingDropIndex = target.Value.InsertIndex;
            ShowDropLine(target.Value.Row, target.Value.Below);
        }

        DndLog($"drag over, pending index {_pendingDropIndex}");
    }

    private void WindowDragLeave(object? sender, RoutedEventArgs e)
    {
        // DragLeave can fire right before Drop, so the pending index is left alone
        // here. Clearing it would snap the drop to the default spot instead of the
        // guide line.
        HideDropLine();
    }

    // Finds the row under the pointer and where a dropped item would land there. The
    // upper half of a row means above it, the lower half below. The menu entry keeps
    // the top spot, so a drop aimed at the very top lands just under it instead.
    // Pointing at the open space under the last row lands after that row. Returns
    // null when the pointer is off the rows entirely.
    private (DataGridRow Row, bool Below, int InsertIndex)? HitTestDropRow(DragEventArgs e)
    {
        try
        {
            var list = _manager.ItemList;

            if (GameGrid == null || !GameGrid.IsVisible)
                return null;

            var pos = e.GetPosition(GameGrid);
            double y = pos.Y;

            DataGridRow? bottomRow = null;
            TownsGame? bottomItem = null;
            double bottomEdge = double.MinValue;

            foreach (var row in GameGrid.GetVisualDescendants().OfType<DataGridRow>())
            {
                // The grid parks recycled rows in the visual tree after items are
                // removed or another card is loaded in the same session. They still
                // hold games that are no longer in the list, so they must not count
                // as drop targets or as the bottom row.
                if (!row.IsVisible)
                    continue;

                if (row.DataContext is not TownsGame hoveredItem)
                    continue;

                int index = list.IndexOf(hoveredItem);
                if (index < 0)
                    continue;

                var rowTop = row.TranslatePoint(new Point(0, 0), GameGrid);
                if (rowTop == null)
                    continue;

                double top = rowTop.Value.Y;
                double height = row.Bounds.Height;

                if (top + height > bottomEdge)
                {
                    bottomEdge = top + height;
                    bottomRow = row;
                    bottomItem = hoveredItem;
                }

                if (y < top || y >= top + height)
                    continue;

                bool below = y > top + height / 2;
                int insertIndex = below ? index + 1 : index;

                if (insertIndex == 0 && list.Count > 0 && list[0].IsMenuItem)
                {
                    insertIndex = 1;
                    below = true;
                }

                return (row, below, Math.Min(insertIndex, list.Count));
            }

            // The pointer is under the rows, either in the grid's empty space or
            // past its bottom edge. Both read as plain white space to the user, so
            // the drop lands after the lowest row, which is the last item since
            // rows fill the view when the list is scrolled partway through.
            if (bottomRow != null && bottomItem != null && y >= bottomEdge &&
                pos.X >= 0 && pos.X < GameGrid.Bounds.Width)
            {
                int index = list.IndexOf(bottomItem);
                if (index >= 0)
                    return (bottomRow, true, Math.Min(index + 1, list.Count));
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ShowDropLine(DataGridRow row, bool below)
    {
        if (DropLine == null || GameGrid == null)
            return;

        var top = row.TranslatePoint(new Point(0, 0), GameGrid);
        if (top == null)
        {
            HideDropLine();
            return;
        }

        double y = top.Value.Y;
        if (below)
            y += row.Bounds.Height;

        DropLine.Margin = new Thickness(0, y - 1, 0, 0);
        DropLine.IsVisible = true;
    }

    private void HideDropLine()
    {
        if (DropLine != null)
            DropLine.IsVisible = false;
    }

    // Fallback spot for a drop that is not over any row. Anything we can't place
    // goes to the end of the list.
    private int DefaultDropIndex()
    {
        return _manager.ItemList.Count;
    }

    // --- Keyboard shortcuts ---

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        // Skip these while editing a cell or typing in the filter box, otherwise
        // Delete removes the selected row instead of editing text.
        if (e.Source is TextBox)
            return;

        if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control)
        {
            if (_manager.UndoManager.CanUndo)
            {
                _manager.UndoManager.Undo();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Y && e.KeyModifiers == KeyModifiers.Control)
        {
            if (_manager.UndoManager.CanRedo)
            {
                _manager.UndoManager.Redo();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Delete && !IsBusy)
        {
            ButtonRemove_Click(null, new RoutedEventArgs());
        }
        else if (e.Key == Key.F2 && !IsBusy && GameGrid.SelectedItem != null)
        {
            GameGrid.BeginEdit();
        }
    }

    // --- Cell editing ---

    private string? _editOldValue;

    private void GameGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.DataContext is TownsGame game && game.IsMenuItem)
        {
            e.Cancel = true;
            return;
        }

        if (e.Row.DataContext is TownsGame g && e.Column.Header?.ToString() == "Title")
        {
            _editOldValue = g.Name;
        }
    }

    private void GameGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Cancel) return;
        if (e.Row.DataContext is not TownsGame game) return;
        if (_editOldValue == null) return;

        if (e.Column.Header?.ToString() == "Title" &&
            e.EditingElement is TextBox tb && _editOldValue != tb.Text)
        {
            _manager.UndoManager.RecordChange(new PropertyEditOperation
            {
                Item = game,
                PropertyName = nameof(TownsGame.Name),
                OldValue = _editOldValue,
                NewValue = tb.Text
            });

            game.TitleDirty = true;
        }

        _editOldValue = null;
    }

    // --- Undo/Redo ---

    private void ButtonUndo_Click(object? sender, RoutedEventArgs e)
    {
        _manager.UndoManager.Undo();
    }

    private void ButtonRedo_Click(object? sender, RoutedEventArgs e)
    {
        _manager.UndoManager.Redo();
    }

    // --- About window ---

    private async void ButtonAbout_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var about = new AboutWindow();
            await about.ShowDialog(this);
        }
        catch { }
    }

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
