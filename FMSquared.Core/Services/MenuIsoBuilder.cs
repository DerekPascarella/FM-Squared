using System.Text;
using FMSquared.Core.Models;

namespace FMSquared.Core.Services;

/// <summary>
/// Rebuilds the Almanac/Spellbook menu ISO in folder 01. Supersedes the
/// scan.exe, mkisofs.exe and fixup.exe toolchain that tools/*/RunMe.bat drove,
/// so the three stages below have to stay in that order.
///   1. Regenerate data/TITLES.TXT from the game list.
///   2. Build the ISO from the data folder with boot_cd in the system area.
///   3. Patch the IO.SYS LBA and sector count into the IPL at offset 0x20.
/// </summary>
public static class MenuIsoBuilder
{
    private const int SectorSize = 2048;

    /// <summary>
    /// Whether folder 01 contains everything needed to rebuild the menu ISO.
    /// </summary>
    public static bool CanBuild(string sdCardPath)
    {
        string menuFolder = Path.Combine(sdCardPath, Constants.MenuFolderName);
        string dataFolder = Path.Combine(menuFolder, Constants.MenuDataFolderName);

        return Directory.Exists(dataFolder) &&
               File.Exists(Path.Combine(menuFolder, Constants.MenuBootFileName)) &&
               File.Exists(Path.Combine(dataFolder, Constants.MenuIoSysName));
    }

    /// <summary>
    /// Rebuilds the menu ISO for the given ODE type. Games must already carry
    /// their final SdNumber values (2..N in menu order).
    /// </summary>
    public static void Build(string sdCardPath, OdeKind odeKind, IEnumerable<TownsGame> games)
    {
        if (!CanBuild(sdCardPath))
            throw new InvalidOperationException(
                "Folder 01 is missing the menu build files (data folder, boot_cd, IO.SYS). " +
                "Copy an Almanac/Spellbook menu installation into folder 01 first.");

        string menuFolder = Path.Combine(sdCardPath, Constants.MenuFolderName);
        string dataFolder = Path.Combine(menuFolder, Constants.MenuDataFolderName);

        // TITLES.TXT is what the menu reads to draw the game list.
        string titlesPath = Path.Combine(dataFolder, Constants.TitlesFileName);
        File.WriteAllText(titlesPath, GenerateTitlesTxt(games), new UTF8Encoding(false));

        // The layout reproduced here is mkisofs -iso-level 1 -G boot_cd -sort layout,
        // which puts the boot_cd IPL in the system area.
        string isoName = odeKind == OdeKind.Wizard
            ? Constants.WizardMenuIsoName
            : Constants.DocBrownMenuIsoName;
        string volumeId = odeKind == OdeKind.Wizard
            ? Constants.WizardVolumeId
            : Constants.DocBrownVolumeId;
        string isoPath = Path.Combine(menuFolder, isoName);
        string bootPath = Path.Combine(menuFolder, Constants.MenuBootFileName);

        // A card should only ever hold one menu ISO. Clear out the other
        // ODE's if a stale one is lying around, since detection would keep
        // picking it up.
        string otherIsoName = odeKind == OdeKind.Wizard
            ? Constants.DocBrownMenuIsoName
            : Constants.WizardMenuIsoName;
        File.Delete(Path.Combine(menuFolder, otherIsoName));

        Iso9660Writer.Build(dataFolder, isoPath, volumeId, bootPath);

        // The IPL cannot find IO.SYS until its LBA is patched in, so this has to
        // run after the ISO is written.
        PatchIplLoader(isoPath);
    }

    /// <summary>
    /// Generates TITLES.TXT content: a count line followed by one
    /// "NNN 0x00000000 Title" line per game, CRLF-terminated.
    /// </summary>
    public static string GenerateTitlesTxt(IEnumerable<TownsGame> games)
    {
        var entries = games.Where(g => !g.IsMenuItem).ToList();

        var sb = new StringBuilder();
        sb.Append(entries.Count).Append("\r\n");

        foreach (var game in entries)
        {
            string number = game.SdNumber.ToString("D3");
            string title = AsciiSanitizer.StripNonPrintableAscii(game.Name).Trim();
            sb.Append(number).Append(" 0x00000000 ").Append(title).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Locates IO.SYS in the ISO 9660 root directory and writes its starting
    /// LBA and 2048-byte sector count as two little-endian 32-bit integers at
    /// absolute offset 0x20, where the IPL loader reads its load parameters.
    /// </summary>
    public static (int lba, int sectors, long bytes) PatchIplLoader(string isoPath, string targetName = "IO.SYS")
    {
        using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite);

        // Read and validate the Primary Volume Descriptor at LBA 16.
        byte[] pvd = new byte[SectorSize];
        stream.Position = 16 * SectorSize;
        stream.ReadExactly(pvd);

        if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001")
            throw new InvalidDataException("Not an ISO 9660 PVD at LBA 16: " + Path.GetFileName(isoPath));

        int rootLba = BitConverter.ToInt32(pvd, 156 + 2);
        int rootSize = BitConverter.ToInt32(pvd, 156 + 10);

        var (fileLba, fileSize) = FindRootEntry(stream, rootLba, rootSize, targetName)
            ?? throw new InvalidDataException($"Could not find {targetName} in ISO root: " + Path.GetFileName(isoPath));

        int sectors = (int)((fileSize + SectorSize - 1) / SectorSize);

        stream.Position = 0x20;
        stream.Write(BitConverter.GetBytes(fileLba));
        stream.Write(BitConverter.GetBytes(sectors));

        return (fileLba, sectors, fileSize);
    }

    // Accepts both NAME and NAME;1, case-insensitively, since either form is
    // valid in a directory extent.
    private static (int lba, long size)? FindRootEntry(Stream stream, int extentLba, int extentSize, string wantedName)
    {
        string wantUpper = wantedName.ToUpperInvariant();

        int remaining = extentSize;
        int lba = extentLba;
        byte[] sector = new byte[SectorSize];

        while (remaining > 0)
        {
            stream.Position = (long)lba * SectorSize;
            stream.ReadExactly(sector);
            lba++;
            remaining -= SectorSize;

            int pos = 0;
            while (pos < SectorSize)
            {
                int recordLength = sector[pos];
                if (recordLength == 0)
                    break;

                int nameLength = sector[pos + 32];
                bool isSpecial = nameLength == 1 && (sector[pos + 33] == 0 || sector[pos + 33] == 1);

                if (!isSpecial)
                {
                    string name = Encoding.ASCII.GetString(sector, pos + 33, nameLength).ToUpperInvariant();
                    if (name == wantUpper || name == wantUpper + ";1")
                    {
                        int fileLba = BitConverter.ToInt32(sector, pos + 2);
                        uint fileSize = BitConverter.ToUInt32(sector, pos + 10);
                        return (fileLba, fileSize);
                    }
                }

                pos += recordLength;
            }
        }

        return null;
    }
}
