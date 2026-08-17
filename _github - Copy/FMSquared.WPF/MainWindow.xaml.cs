using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FMSquared.Core;
using FMSquared.Core.Models;
using FMSquared.Core.Services;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using GongSolutions.Wpf.DragDrop.Utilities;

namespace FMSquared;

public partial class MainWindow : Window, GongSolutions.Wpf.DragDrop.IDropTarget, INotifyPropertyChanged
{
    private readonly Manager _manager = new();
    private readonly AppSettings _settings;
    private bool _suppressMenuTypeChange;

    // Root path of each drive in DriveComboBox, by index. The combo items are
    // display labels and the path can't be recovered from them.
    private readonly List<string> _drivePaths = new();

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
        set { _customSdPath = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(IsUsingCustomPath)); RaisePropertyChanged(nameof(HasSdPath)); }
    }

    public bool HasSdPath => !string.IsNullOrEmpty(_manager.SdCardPath);

    private bool _isFilterActive;
    public bool IsFilterActive
    {
        get => _isFilterActive;
        set { _isFilterActive = value; RaisePropertyChanged(); }
    }

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

    public event PropertyChangedEventHandler? PropertyChanged;

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

        _manager.OnFolderLocked = (path) =>
        {
            var result = MessageBox.Show(this,
                $"The following folder is open in another program:\n\n{path}\n\n" +
                "Close any programs using it, then click Yes to retry.",
                "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return Task.FromResult(result == MessageBoxResult.Yes);
        };

        _manager.OnArchiveWarning = (message) =>
        {
            MessageBox.Show(this, message, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return Task.CompletedTask;
        };

        this.Loaded += MainWindow_Loaded;
        this.Closing += MainWindow_Closing;
        this.PreviewKeyDown += MainWindow_PreviewKeyDown;

        RefreshDriveList();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var readOnlyPath = AppSettings.CheckReadOnly();
            if (readOnlyPath != null)
            {
                MessageBox.Show(this,
                    $"The settings file is marked as read-only:\n\n{readOnlyPath}\n\n" +
                    "Your preferences will not be saved until this is resolved.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                manualDialog.Owner = this;
                manualDialog.ShowDialog();
            }
            else if (result.UpdateAvailable && !UpdateAvailableDialog.ShouldSkipVersion(result.LatestTag))
            {
                var dialog = new UpdateAvailableDialog(result.LatestTag, result.LatestVersion);
                dialog.Owner = this;
                dialog.ShowDialog();

                if (dialog.UserWantsUpdate)
                {
                    var wizard = new UpdateWizardWindow(result.LatestTag, result.LatestVersion);
                    wizard.Owner = this;
                    wizard.ShowDialog();
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
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
    }

    private void SaveSettings()
    {
        _settings.EnableLockCheck = LockCheckBox.IsChecked == true;
        _settings.TempFolder = NormalizeTempFolderForSave(TempFolderTextBox.Text);
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
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

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
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
                        string root = drive.RootDirectory.FullName;
                        if (File.Exists(Path.Combine(root, Constants.DocBrownIniFile)) ||
                            File.Exists(Path.Combine(root, Constants.WizardIniFile)))
                        {
                            autoSelectIndex = index;
                        }
                    }
                    catch { }
                }

                index++;
            }
        }

        if (autoSelectIndex >= 0)
            DriveComboBox.SelectedIndex = autoSelectIndex;
    }

    private void ButtonRefreshDrives_Click(object sender, RoutedEventArgs e) => RefreshDriveList();

    private async void ButtonBrowseSdPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select SD card or folder",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        string folderPath = dialog.SelectedPath;

        IsUsingCustomPath = true;
        CustomSdPath = folderPath;
        DriveComboBox.SelectedIndex = -1;

        _manager.ToolsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
        _manager.SdCardPath = folderPath;

        RaisePropertyChanged(nameof(HasSdPath));
        await LoadCard();
    }

    private void ButtonBrowseTempFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select temporary folder",
            UseDescriptionForTitle = true,
            SelectedPath = TempFolderTextBox.Text ?? ""
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        TempFolderTextBox.Text = dialog.SelectedPath;
        SaveSettings();
    }

    private void ButtonResetTempFolder_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Reset the Temporary Folder path to default?",
            "Confirmation",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        TempFolderTextBox.Text = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        SaveSettings();
    }

    private async void DriveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int selectedIndex = DriveComboBox.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= _drivePaths.Count || IsBusy) return;

        string drivePath = _drivePaths[selectedIndex];

        IsUsingCustomPath = false;
        CustomSdPath = string.Empty;

        _manager.ToolsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools");
        _manager.SdCardPath = drivePath;

        RaisePropertyChanged(nameof(HasSdPath));
        await LoadCard();
    }

    private async Task LoadCard()
    {
        IsBusy = true;
        FilterTextBox.Text = string.Empty;
        IsFilterActive = false;

        try
        {
            await _manager.LoadItemsFromCardAsync();

            UpdateGamesListHeader();
            UpdateOdeDisplay();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void MenuType_Changed(object sender, RoutedEventArgs e)
    {
        // The default radio raises Checked while the window is still being
        // parsed, before the other radio exists.
        if (_suppressMenuTypeChange || RadioSpellbook == null) return;

        _manager.OdeKindSelected = RadioSpellbook.IsChecked == true
            ? OdeKind.Wizard
            : OdeKind.DocBrown;
    }

    // --- Game list operations ---

    private void ButtonAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select disc image file(s)",
            Multiselect = true,
            Filter = "FM Towns Disc Images (*.cdi;*.mdf;*.mds;*.img;*.bin;*.iso;*.ccd;*.cue;*.chd;*.7z;*.rar;*.zip)|*.cdi;*.mdf;*.mds;*.img;*.bin;*.iso;*.ccd;*.cue;*.chd;*.7z;*.rar;*.zip|All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true) return;

        _ = AddGamesFromPaths(dialog.FileNames);
    }

    private async Task AddGamesFromPaths(string[] paths, int insertIndex = -1)
    {
        IsBusy = true;

        ProgressWindow? progressWindow = null;
        if (paths.Length > 1)
        {
            progressWindow = new ProgressWindow();
            progressWindow.Owner = this;
            progressWindow.Title = "Adding Disc Images";
            progressWindow.IsIndeterminate = true;
        }

        try
        {
            // Shown on the first report, so a fast add never flashes it.
            var progress = new Progress<string>(msg => Dispatcher.Invoke(() =>
            {
                if (progressWindow != null)
                {
                    if (!progressWindow.IsVisible)
                        progressWindow.Show();
                    progressWindow.TextContent = msg;
                }
            }));

            await _manager.AddGamesAsync(paths, progress, insertIndex);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void ButtonRemove_Click(object sender, RoutedEventArgs e)
    {
        var selected = GameGrid.SelectedItems?.Cast<TownsGame>().ToList();
        if (selected == null || selected.Count == 0) return;

        _manager.RemoveItems(selected);
    }

    private void ButtonFloppyBoot_Click(object sender, RoutedEventArgs e)
    {
        if (_manager.HasFloppyBootEntry)
        {
            MessageBox.Show(this, "The list already contains a floppy boot entry.",
                "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new FloppyBootWindow();
        dialog.Owner = this;

        if (dialog.ShowDialog() == true)
            _manager.InsertFloppyBootEntry();
    }

    private void ButtonMoveUp_Click(object sender, RoutedEventArgs e)
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

    private void ButtonMoveDown_Click(object sender, RoutedEventArgs e)
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

    private void ButtonSort_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "Your disc images will be sorted alphabetically by title.\n\nProceed?",
            "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _manager.SortList();
    }

    private void ButtonSearch_Click(object sender, RoutedEventArgs e)
    {
        string filterText = FilterTextBox.Text.Trim();
        if (_manager.ItemList.Count == 0 || string.IsNullOrWhiteSpace(filterText))
            return;

        int startIndex = GameGrid.SelectedIndex == -1 ? 0 : GameGrid.SelectedIndex;

        if (!SearchInGrid(startIndex, filterText))
        {
            if (!SearchInGrid(0, filterText))
                MessageBox.Show(this, "No matches found.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private bool SearchInGrid(int start, string filter)
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(GameGrid.ItemsSource);
        if (view == null) return false;

        var visibleItems = view.Cast<TownsGame>().ToList();

        for (int i = start; i < visibleItems.Count; i++)
        {
            var item = visibleItems[i];
            if (GameGrid.SelectedItem != item && _manager.SearchInItem(item, filter))
            {
                GameGrid.SelectedItem = item;
                GameGrid.ScrollIntoView(item);
                return true;
            }
        }

        return false;
    }

    private void ButtonFilter_Click(object sender, RoutedEventArgs e)
    {
        string filterText = FilterTextBox.Text.Trim();
        if (string.IsNullOrEmpty(filterText))
            return;

        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(GameGrid.ItemsSource);
        if (view == null) return;

        view.Filter = obj => obj is TownsGame item &&
            (item.IsMenuItem ||
             (item.Name?.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0));

        IsFilterActive = true;
    }

    private void ButtonFilterReset_Click(object sender, RoutedEventArgs e)
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(GameGrid.ItemsSource);
        if (view == null) return;

        view.Filter = null;
        FilterTextBox.Text = string.Empty;
        IsFilterActive = false;
    }

    private void UpdateSearchMatches()
    {
        var text = _searchText.Trim();
        foreach (var item in _manager.ItemList)
            item.IsMatch = text.Length > 0 && _manager.SearchInItem(item, text);
    }

    private async void ButtonSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_manager.SdCardPath))
        {
            MessageBox.Show(this, "No SD card selected.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirmResult = MessageBox.Show(this,
            $"Save changes to \"{_manager.SdCardPath}\" drive?\n\n" +
            "Game folders will be renumbered to match the list order.",
            "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirmResult != MessageBoxResult.Yes) return;

        var spaceCheck = await _manager.CalculateRequiredSpaceAsync();
        if (!spaceCheck.HasSufficientSpace)
        {
            var proceed = MessageBox.Show(this,
                Manager.BuildSpaceWarningMessage(spaceCheck),
                "Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes) return;
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
            progressWindow.Owner = this;
            progressWindow.TotalItems = _manager.ItemList.Count(g => !g.IsMenuItem);
            progressWindow.IsIndeterminate = true;
            progressWindow.Show();

            try
            {
                var progress = new Progress<string>(msg => Dispatcher.Invoke(() =>
                {
                    progressWindow.TextContent = msg;
                }));

                var itemProgress = new Progress<int>(count => Dispatcher.Invoke(() =>
                {
                    progressWindow.ProcessedItems = count;
                }));

                string tempRoot = GetTempFolderRoot();
                await _manager.SaveAsync(progress, itemProgress, string.IsNullOrEmpty(tempRoot) ? null : tempRoot);

                SaveSettings();

                progressWindow.AllowClose();
                progressWindow.Close();

                MessageBox.Show("Done!", "Information", MessageBoxButton.OK, MessageBoxImage.Information);

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
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            lockProgress.Owner = this;
            lockProgress.TextContent = "Checking for locked files and folders...";
            lockProgress.TotalItems = paths.Count;
            lockProgress.Show();

            Dictionary<string, string> locked;
            try
            {
                var progress = new Progress<(int current, int total, string name)>(info =>
                    Dispatcher.Invoke(() =>
                    {
                        lockProgress.ProcessedItems = info.current;
                    }));

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
            dialog.Owner = this;
            dialog.ShowDialog();

            if (!dialog.UserWantsRetry)
                return false;
        }
    }

    // --- Context menu ---

    private void MenuItemRename_Click(object sender, RoutedEventArgs e)
    {
        if (GameGrid.SelectedItem != null)
            GameGrid.BeginEdit();
    }

    private void MenuItemTitleCase_Click(object sender, RoutedEventArgs e)
    {
        RenameSelectedItems(name => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLowerInvariant()));
    }

    private void MenuItemUppercase_Click(object sender, RoutedEventArgs e)
    {
        RenameSelectedItems(name => name.ToUpperInvariant());
    }

    private void MenuItemLowercase_Click(object sender, RoutedEventArgs e)
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

    private void MenuItemRenameFolder_Click(object sender, RoutedEventArgs e)
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

    private void MenuItemRenameFile_Click(object sender, RoutedEventArgs e)
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

    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

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

    // --- Drag and drop (Window-level kept for drop outside DataGrid) ---

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (IsBusy || IsFilterActive || !HasSdPath || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (IsBusy || IsFilterActive || !HasSdPath) return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            await AddGamesFromPaths(paths);
        }
    }

    // --- Keyboard shortcuts ---

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Skip these while editing a cell or typing in the filter box. PreviewKeyDown
        // tunnels to the window first, so otherwise Delete wipes the row mid-edit.
        if (_isCellEditing || e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase)
            return;

        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_manager.UndoManager.CanUndo)
            {
                _manager.UndoManager.Undo();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_manager.UndoManager.CanRedo)
            {
                _manager.UndoManager.Redo();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Delete && !IsBusy)
        {
            ButtonRemove_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.F2 && !IsBusy && GameGrid.SelectedItem != null)
        {
            GameGrid.BeginEdit();
        }
    }

    // --- Cell editing ---

    private string? _editOldValue;
    private bool _isCellEditing;

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

        // Editing started (the cancel case above returned early).
        _isCellEditing = true;
    }

    private void GameGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        // Editing ended, whatever the outcome.
        _isCellEditing = false;

        if (e.EditAction == DataGridEditAction.Cancel) return;
        if (e.Row.DataContext is not TownsGame game) return;
        if (_editOldValue == null) return;

        string? newValue = null;

        if (e.Column.Header?.ToString() == "Title" &&
            e.EditingElement is System.Windows.Controls.ContentPresenter cp)
        {
            var tb = FindVisualChild<System.Windows.Controls.TextBox>(cp);
            if (tb != null)
                newValue = tb.Text;
        }

        if (newValue != null && _editOldValue != newValue)
        {
            _manager.UndoManager.RecordChange(new PropertyEditOperation
            {
                Item = game,
                PropertyName = nameof(TownsGame.Name),
                OldValue = _editOldValue,
                NewValue = newValue
            });

            game.TitleDirty = true;
        }

        _editOldValue = null;
    }

    private void GameGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.Column.Header?.ToString() != "Title") return;
        if (e.EditingElement is not System.Windows.Controls.ContentPresenter cp) return;

        // Unlike a text column's editor, the template's TextBox is not focused
        // automatically, and its visual tree may not exist yet.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            var tb = FindVisualChild<System.Windows.Controls.TextBox>(cp);
            if (tb != null)
            {
                tb.Focus();
                tb.SelectAll();
            }
        }));
    }

    private static T? FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    // --- Undo/Redo ---

    private void ButtonUndo_Click(object sender, RoutedEventArgs e)
    {
        _manager.UndoManager.Undo();
    }

    private void ButtonRedo_Click(object sender, RoutedEventArgs e)
    {
        _manager.UndoManager.Redo();
    }

    // --- DataGrid row drag-reorder and file drop (IDropTarget) ---

    void GongSolutions.Wpf.DragDrop.IDropTarget.DragOver(GongSolutions.Wpf.DragDrop.IDropInfo dropInfo)
    {
        if (dropInfo == null || IsFilterActive) return;

        if (dropInfo.DragInfo == null)
        {
            // External file drop
            if (dropInfo.Data is System.Windows.DataObject data && data.ContainsFileDropList())
            {
                if (dropInfo.UnfilteredInsertIndex == 0 && _manager.ItemList.Count > 0 && _manager.ItemList[0].IsMenuItem)
                    dropInfo.Effects = System.Windows.DragDropEffects.None;
                else
                    dropInfo.Effects = System.Windows.DragDropEffects.Copy;
            }
        }
        else if (GongSolutions.Wpf.DragDrop.DefaultDropHandler.CanAcceptData(dropInfo))
        {
            // Internal row reorder
            var draggedItems = GongSolutions.Wpf.DragDrop.DefaultDropHandler
                .ExtractData(dropInfo.Data).OfType<TownsGame>().ToList();

            bool hasMenuItem = draggedItems.Any(g => g.IsMenuItem);

            if (hasMenuItem || dropInfo.UnfilteredInsertIndex == 0)
                dropInfo.Effects = System.Windows.DragDropEffects.None;
            else
                dropInfo.Effects = System.Windows.DragDropEffects.Move;
        }

        if (dropInfo.Effects != System.Windows.DragDropEffects.None)
            dropInfo.DropTargetAdorner = GongSolutions.Wpf.DragDrop.DropTargetAdorners.Insert;
    }

    async void GongSolutions.Wpf.DragDrop.IDropTarget.Drop(GongSolutions.Wpf.DragDrop.IDropInfo dropInfo)
    {
        if (dropInfo == null || IsFilterActive) return;

        if (dropInfo.DragInfo == null)
        {
            // External file drop, insert at drop position
            if (dropInfo.Data is System.Windows.DataObject data && data.ContainsFileDropList())
            {
                var paths = data.GetFileDropList().Cast<string>().ToArray();
                int dropIndex = dropInfo.UnfilteredInsertIndex;
                await AddGamesFromPaths(paths, dropIndex);
            }
            return;
        }

        // Internal row reorder
        var draggedItems = GongSolutions.Wpf.DragDrop.DefaultDropHandler
            .ExtractData(dropInfo.Data).OfType<TownsGame>().ToList();

        if (draggedItems.Count == 0) return;

        var oldOrder = _manager.ItemList.ToList();

        var items = GongSolutions.Wpf.DragDrop.DefaultDropHandler
            .ExtractData(dropInfo.Data).OfType<object>().ToList();

        int insertIndex = dropInfo.UnfilteredInsertIndex;
        var sourceList = dropInfo.DragInfo.SourceCollection.TryGetList();
        var destList = dropInfo.TargetCollection.TryGetList();

        if (sourceList != null)
        {
            foreach (var o in items)
            {
                int index = sourceList.IndexOf(o);
                if (index != -1)
                {
                    sourceList.RemoveAt(index);
                    if (destList != null && Equals(sourceList, destList) && index < insertIndex)
                        --insertIndex;
                }
            }
        }

        if (destList != null)
        {
            foreach (var o in items)
                destList.Insert(insertIndex++, o);
        }

        var newOrder = _manager.ItemList.ToList();
        _manager.UndoManager.RecordChange(new ListReorderOperation("Move Items")
        {
            ItemList = _manager.ItemList,
            OldOrder = oldOrder,
            NewOrder = newOrder
        });
    }

    // --- About window ---

    private void ButtonAbout_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow();
        about.Owner = this;
        about.ShowDialog();
    }

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
