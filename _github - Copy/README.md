# FM^2
<img align="right" src="https://raw.githubusercontent.com/DerekPascarella/FM-Squared/refs/heads/main/screenshots/screenshot.png" width="265">FM^2 (FM Squared, the Fujitsu Micro File Manager) is an SD card management tool for the FM Towns/FM Towns Marty ODEs [Wizard](https://gdemu.wordpress.com/details/wizard-details/) and [DocBrown](https://gdemu.wordpress.com/details/docbrown-details/).

As of version 2.0.0, FM^2 has been renamed (formerly DocBrown/Wizard Sorter) and completely rewritten from a command-line tool to a cross-platform GUI application.

The GUI design and workflow are inspired by [GD MENU Card Manager](https://github.com/sonik-br/GDMENUCardManager) by Sonik, the equivalent SD card management tool for SEGA Dreamcast GDEMU users, which was also used for the [openMenu Virtual Folder Bundle](https://github.com/DerekPascarella/openMenu-Virtual-Folder-Bundle) and [Orbital Organizer](https://github.com/DerekPascarella/Orbital-Organizer).

Please note that DocBrown/Wizard SD cards must be formatted as FAT32.

## Table of Contents

- [Current Version](#current-version)
- [Changelog](#changelog)
- [Credits](#credits)
- [Supported Platforms](#supported-platforms)
- [Supported Disc Image Formats](#supported-disc-image-formats)
- [Basic Usage](#basic-usage)
  - [Loading an SD Card](#loading-an-sd-card)
  - [Adding Games](#adding-games)
  - [Removing Games](#removing-games)
  - [Editing Game Information](#editing-game-information)
  - [Reordering Games](#reordering-games)
  - [Inserting a Floppy Boot Entry](#inserting-a-floppy-boot-entry)
  - [Searching and Filtering](#searching-and-filtering)
  - [Saving Changes](#saving-changes)
  - [Undo and Redo](#undo-and-redo)
- [Menu Type Options](#menu-type-options)
- [SD Card Compatibility](#sd-card-compatibility)
- [Legal and Licensing](#legal-and-licensing)
  - [FM^2](#fm2-1)
  - [Almanac and Spellbook](#almanac-and-spellbook)
  - [Third-Party Components](#third-party-components)

## Current Version
FM^2 is currently at version [2.0.0](https://github.com/DerekPascarella/FM-Squared/releases/tag/2.0.0).

## Changelog
- **Version 2.0.0 (2026-08-17)**
  - Complete rewrite from console application (DocBrown/Wizard Sorter) to cross-platform GUI (Windows, macOS, Linux), renamed FM^2.
  - Almanac and Spellbook now bundled, so users no longer need to source either menu system themselves.
  - Menu ISO (`ALMANAC.ISO`/`SPLLBOOK.ISO`) is now rebuilt natively by FM^2 itself, no longer requiring the original `RunMe.bat` toolchain.
  - Game list order is now fully user-controlled, with the ability to either manually sort or automatically alphanumerically sort, instead of forced alphanumeric sorting.
  - Compressed archives (ZIP, 7z, RAR) containing disc images can now be added directly.
  - CUE-based disc images are automatically converted to CloneCD format (CCD/IMG/SUB) when saved to the SD card.
  - CHD disc images are supported and automatically decompressed to CUE/BIN before conversion to CCD/IMG/SUB.
  - Dedicated button added for inserting a "Boot From Floppy" menu entry.
  - Folders containing neither a disc image nor a `Title.txt` file are now ignored and left untouched, instead of being renamed to `INVALID_X`.
  - Auto-update functionality added for Windows and Linux builds (macOS presently only supports an update notification).
- **Version 1.4 (2025-09-08)**
  - Game labels can now be modified in `GameList.txt` before processing SD card instead of solely by modifying `Title.txt` metadata text files inside of numbered folders.
  - If files/folders are locked by another process when DocBrown/Wizard Sorter attempts to move/rename them, a prompt will now be displayed giving the user the opportunity to close said processes before proceeding, instead of those locked files/folders being skipped.
- **Version 1.3 (2025-05-06)**
  - Improved clarity of status message output when new disc images are added and processed.
- **Version 1.2 (2025-02-19)**
  - Cleaned up status message output to be more compact and descriptive.
  - Enhanced sanity-check for Almanac/Spellbook rebuild.
  - Invalid user response to Almanac/Spellbook rebuild prompt now properly handled.
- **Version 1.1 (2023-05-03)**
  - To force Almanac/Spellbook to properly index a game list exceeding 100 even when using FindFirstFile(), a "FAT sort" is now performed on target SD card (e.g., `20 200 21` now becomes `20 21 ... 200`).
- **Version 1.0 (2023-03-24)**
  - Initial release.

## Credits

- **Programming**
  - Derek Pascarella (ateam)
- **Testing**
  - Josh (hasnopants)
- **Special Thanks**
  - Sonik for his work on [GD MENU Card Manager](https://github.com/sonik-br/GDMENUCardManager), upon which the FM^2 GUI is based

## Supported Platforms

| Platform | Architecture | Download | Notes |
|----------|-------------|----------|-------|
| Windows | x64 | `.zip` | Requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) |
| Windows | x86 | `.zip` | Requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) |
| macOS | Apple Silicon | `.tar.gz` (`.app` bundle) | Self-contained, no runtime needed |
| macOS | Intel | `.tar.gz` (`.app` bundle) | Self-contained, no runtime needed |
| Linux | x64 | `.tar.gz` | Self-contained, no runtime needed |

## Supported Disc Image Formats

| Format | Extension(s) | Notes |
|--------|-------------|-------|
| ISO 9660 | `.iso` | Single-file disc image |
| DiscJuggler | `.cdi` | Single-file disc image |
| Alcohol 120% | `.mds`, `.mdf` | Two-file set |
| CloneCD | `.ccd`, `.img`, `.sub` | Three-file set |
| CUE-based | `.cue` (+ `.bin`, `.iso`, `.wav`, etc.) | Automatically converted to CCD/IMG/SUB |
| CHD | `.chd` | Decompressed to CUE/BIN then converted to CCD/IMG/SUB |
| Compressed | `.zip`, `.7z`, `.rar` | Archives containing any of the above formats |

## Basic Usage

### Loading an SD Card
Select the SD card drive from the **SD Drive** dropdown, or click the folder icon to browse for a custom folder. Once selected, FM^2 scans numbered folders on the card and reads the `Title.txt` metadata file from each game folder. If a game folder has no `Title.txt`, the base file name of its disc image is used as the game title instead, with Redump-style tags automatically stripped.

The **Temp. Folder** setting controls where temporary files are stored during operations. By default, the system temp directory is used.

### Adding Games
New games can be added by clicking the **+** button or by dragging disc image files, folders, or compressed archives directly onto the game list. Added games appear with "Other" in the **Location** column until changes are saved to the SD card.

By default, newly added games are titled using the disc image's base file name, with Redump-style tags automatically stripped.

### Removing Games
Select one or more games in the list and click the **-** button. The corresponding numbered folders are deleted from the SD card when changes are saved.

### Editing Game Information
Double-click a cell in the **Title** column to edit its value inline.

Multiple games can be selected at once for bulk operations. Right-clicking opens a context menu with the following options, most of which support multi-select:
- **Rename** - Rename the selected game title (single selection only).
- **Title Case** / **Uppercase** / **Lowercase** - Change the case of all selected game titles.
- **Automatically Rename Title** - Rename all selected titles using one of two sources:
  - **Using its folder name on computer** - Use the name of each source folder on the computer (only available for newly added games).
  - **Using disc image's base file name** - Use the file name of each disc image.

### Reordering Games
Use the **up** and **down** arrow buttons to move a selected game in the list. Games can also be reordered via drag-and-drop. The **Sort List** button sorts all games alphanumerically by title.

The Almanac and Spellbook menu systems display games in the order they appear in the list, so games can be arranged in any order desired, not just alphabetically.

### Inserting a Floppy Boot Entry
The **Insert Floppy Boot Entry** button adds a special "---Boot From Floppy---" entry to the top of the game list. This entry occupies a numbered folder containing no disc image, and launching it from the Almanac/Spellbook menu causes the FM Towns to boot from floppy disk. This is useful for software that must be started from FDD while the ODE is installed.

### Searching and Filtering
The **Search/Filter** text box accepts search terms that match against game titles.
- The **search** button (magnifying glass) navigates to the next matching entry in the list.
- The **filter** button (funnel) hides all non-matching entries, showing only games that match the search term.
- The **reset** button clears the filter and restores the full game list.

### Saving Changes
Clicking **Save Changes** writes all pending changes to the SD card. This includes:
- Installing the bundled Almanac/Spellbook menu files into folder `01` if not already present.
- Renumbering game folders sequentially (starting from `02`) to match the list order.
- Performing a FAT sort so the menu displays games in the correct order.
- Copying new game files to the SD card.
- Converting CUE-based disc images to CloneCD format and decompressing CHD disc images.
- Rebuilding the Almanac/Spellbook menu ISO in folder `01`.
- Generating `GameList.txt` with a formatted list of all games on the card.
- Writing or updating the `Title.txt` metadata file in each game folder.
- Copying a default `DocBrown.ini`/`Wizard.ini` settings file to the card root if not already present.
- Removing orphaned numbered folders that no longer correspond to any game in the list.

The **File/Folder Lock Check** checkbox enables a pre-save scan that checks for files or folders locked by another process. If locked files are detected, a dialog is displayed listing them so they can be closed before proceeding.

### Undo and Redo
The **Undo** and **Redo** buttons support up to 10 levels of undo/redo for all list operations (adding, removing, reordering, editing).

## Menu Type Options

The **Menu Type** setting controls which menu system FM^2 targets when building the SD card. Two options are available:

- **Almanac** - The [Almanac](https://gdemu.wordpress.com/operation/docbrown-operation/) menu system for the DocBrown ODE, used with the FM Towns Marty.
- **Spellbook** - The [Spellbook](https://gdemu.wordpress.com/operation/wizard-operation/) menu system for the Wizard ODE, used with FM Towns computers.

When an SD card is loaded, FM^2 automatically detects which menu system it contains and selects the appropriate option. For new or empty SD cards, the menu type can be selected manually.

FM^2 ships with a bundled copy of both menu systems, which is installed to folder `01` automatically when changes are saved. Users no longer need to source Almanac or Spellbook themselves.

## SD Card Compatibility

FM^2 works with any existing DocBrown/Wizard SD card out of the box, regardless of how it was previously set up or managed. This includes SD cards built manually, SD cards managed by the original `RunMe.bat` rebuild process, and SD cards managed by the legacy DocBrown/Wizard Sorter console tool.

When loading an SD card, FM^2 reads the `Title.txt` metadata file in each numbered folder to recover game titles, falling back on disc image file names where no such file exists. Folders containing neither a disc image nor a `Title.txt` file are ignored and left untouched. No manual migration or preparation is needed.

## Legal and Licensing

### FM^2
**Copyright (C) 2026, Derek Pascarella (ateam)**

Licensed under the GNU General Public License v3.0 (GPL-3.0).

Repository: https://github.com/DerekPascarella/FM-Squared

The GUI design and workflow of FM^2 are inspired by [GD MENU Card Manager](https://github.com/sonik-br/GDMENUCardManager) by Sonik (GPL-3.0), the equivalent SD card management tool for SEGA Dreamcast GDEMU users.

For the full license text, see `LICENSE`.

### Almanac and Spellbook
The Almanac and Spellbook menu systems were originally created by [Deunan](https://gdemu.wordpress.com/), developer of the DocBrown and Wizard ODEs. FM^2 bundles both menu systems and rebuilds their menu ISO images for DocBrown/Wizard compatibility.

### Third-Party Components
- [Avalonia UI](https://avaloniaui.net/) (MIT) - cross-platform GUI framework
- [SharpCompress](https://github.com/adamhathcock/sharpcompress) (MIT) - archive extraction
- [libchdr](https://github.com/rtissera/libchdr) (BSD-3-Clause) - CHD decompression
- [ByteSize](https://github.com/omar/ByteSize) (MIT) - file size formatting
- [gong-wpf-dragdrop](https://github.com/punker76/gong-wpf-dragdrop) (BSD-3-Clause) - drag-and-drop support in the Windows UI
