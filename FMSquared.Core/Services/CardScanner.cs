using FMSquared.Core.Models;

namespace FMSquared.Core.Services;

/// <summary>
/// Scans an SD card for FM Towns games. A folder counts as a game when it
/// has a Title.txt or contains a recognized disc image anywhere inside it.
/// Folders that have neither are ignored and left untouched, except numbered
/// ones, which get cleaned up by orphan deletion on save.
/// </summary>
public static class CardScanner
{
    /// <summary>
    /// Result of a card scan.
    /// </summary>
    public sealed class ScanResult
    {
        public int GameCount { get; set; }
    }

    /// <summary>
    /// Scans the SD card for games, invoking a callback as each is loaded.
    /// Folder 01 (menu), system folders, and INVALID_* folders are skipped.
    /// Numbered folders are visited in numeric order, everything else after
    /// them alphabetically.
    /// </summary>
    public static async Task<ScanResult> ScanCardAsync(string sdCardPath, Action<TownsGame> onGameLoaded)
    {
        var result = new ScanResult();

        var directories = Directory.GetDirectories(sdCardPath)
            .Select(d => new { Path = d, Name = Path.GetFileName(d) })
            .Where(d => !d.Name.StartsWith('.'))
            .Where(d => d.Name != Constants.MenuFolderName)
            .Where(d => !d.Name.StartsWith(Constants.InvalidFolderPrefix, StringComparison.OrdinalIgnoreCase))
            .Where(d => !Constants.IgnoredFolderNames.Contains(d.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(d => int.TryParse(d.Name, out _) ? 0 : 1)
            .ThenBy(d => int.TryParse(d.Name, out int n) ? n : 0)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var dir in directories)
        {
            var game = await Task.Run(() => LoadGameFromFolder(dir.Path, dir.Name));

            if (game == null)
                continue;

            onGameLoaded(game);
            result.GameCount++;
        }

        return result;
    }

    // Null when the folder holds neither a Title.txt nor a disc image.
    private static TownsGame? LoadGameFromFolder(string folderPath, string folderName)
    {
        string titlePath = Path.Combine(folderPath, Constants.TitleFile);
        bool hasTitle = File.Exists(titlePath);

        // Disc images anywhere inside the folder count, including subfolders.
        var allFiles = Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories).ToList();
        bool hasDiscImage = allFiles.Any(f =>
            Constants.DiscImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) ||
            Constants.CloneCdExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

        if (!hasTitle && !hasDiscImage)
            return null;

        var game = new TownsGame
        {
            FullFolderPath = folderPath,
            IsPlaceholder = hasTitle && !hasDiscImage,
            WorkMode = WorkMode.None
        };

        if (hasTitle)
        {
            game.Name = File.ReadAllText(titlePath).Trim();
        }

        game.SdNumber = int.TryParse(folderName, out int number) ? number : 0;

        PopulateImageFiles(game, allFiles);

        // Untitled folders on a processed card are just numbers, which make
        // poor titles. Prefer the disc image's base file name when available.
        if (string.IsNullOrWhiteSpace(game.Name))
        {
            game.Name = game.ImageFiles.Count > 0
                ? NameSanitizer.Sanitize(Path.GetFileNameWithoutExtension(game.ImageFiles[0]))
                : System.Text.RegularExpressions.Regex.Replace(folderName.Trim(), @"\s+", " ");
            game.TitleDirty = true;
        }

        return game;
    }

    /// <summary>
    /// Checks whether a folder contains any recognized disc image files.
    /// </summary>
    public static bool HasDiscImage(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return false;

        foreach (var file in Directory.GetFiles(folderPath))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (Constants.AllImageExtensions.Contains(ext) || Constants.ChdExtensions.Contains(ext))
                return true;
        }

        return false;
    }

    // Takes the caller's file list rather than walking the folder again. Sets Length too.
    private static void PopulateImageFiles(TownsGame game, List<string> files)
    {
        game.ImageFiles.Clear();
        long totalSize = 0;

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();

            if (Constants.AllImageExtensions.Contains(ext) || ext == ".sub" || ext == ".mdf")
            {
                game.ImageFiles.Add(file);
                totalSize += new FileInfo(file).Length;
            }
        }

        game.Length = game.IsPlaceholder ? -1 : totalSize;

        if (game.ImageFiles.Any(f => Path.GetExtension(f).Equals(".ccd", StringComparison.OrdinalIgnoreCase)))
            game.FileFormat = FileFormat.CloneCd;
        else if (game.ImageFiles.Any(f => Path.GetExtension(f).Equals(".cue", StringComparison.OrdinalIgnoreCase)))
            game.FileFormat = FileFormat.CueBin;
        else
            game.FileFormat = FileFormat.Uncompressed;
    }
}
