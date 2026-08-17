using System.Text;

namespace FMSquared.Core.Services;

/// <summary>
/// Minimal ISO 9660 level-1 image writer that reproduces the exact disc layout
/// mkisofs produces for the Almanac/Spellbook menu discs:
///   sectors 0-15   system area (boot IPL, zero-padded)
///   sector  16     Primary Volume Descriptor
///   sector  17     Volume Descriptor Set Terminator
///   sector  18     (reserved, zeroed)
///   sectors 19-20  L path table
///   sectors 21-22  M path table
///   sector  23+    directories (root first, then subdirectories)
///   then           file extents: IO.SYS first, EMPTY.BIN last, everything
///                  else in directory-traversal order (mkisofs -sort layout)
///   tail           150 sectors of zero padding (mkisofs default)
/// </summary>
public static class Iso9660Writer
{
    private const int SectorSize = 2048;
    private const int PadSectors = 150;

    private sealed class Entry
    {
        public string Name = string.Empty;       // ISO identifier, no ";1"
        public string? SourcePath;               // null for directories
        public long Size;
        public int Lba;
        public bool IsDirectory;
        public Entry? Parent;
        public List<Entry> Children = new();
        public int PathTableIndex;               // 1-based, directories only
        public DateTime Timestamp;
    }

    /// <summary>
    /// Builds an ISO 9660 image from a content directory.
    /// </summary>
    /// <param name="contentDirectory">Contents become the disc root, not a subdirectory.</param>
    /// <param name="bootIplPath">Optional. Written into the system area, like mkisofs -G.</param>
    public static void Build(string contentDirectory, string outputIsoPath, string volumeId, string? bootIplPath)
    {
        var root = BuildTree(contentDirectory);

        // Directories get the first extents (root, then subdirectories in
        // breadth-first order, matching mkisofs path table numbering).
        var directories = new List<Entry>();
        var queue = new Queue<Entry>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            dir.PathTableIndex = directories.Count + 1;
            directories.Add(dir);
            foreach (var child in dir.Children.Where(c => c.IsDirectory))
                queue.Enqueue(child);
        }

        // Files in breadth-first directory order, with IO.SYS forced first and
        // EMPTY.BIN forced last (the "layout" sort file behavior).
        var files = new List<Entry>();
        foreach (var dir in directories)
            files.AddRange(dir.Children.Where(c => !c.IsDirectory));
        var ordered = files
            .OrderBy(f => f.Name.Equals(Constants.MenuIoSysName, StringComparison.OrdinalIgnoreCase) ? 0
                        : f.Name.Equals(Constants.MenuEmptyBinName, StringComparison.OrdinalIgnoreCase) ? 2 : 1)
            .ToList();

        // Assign extents. Directory records for this disc always fit one sector.
        int nextLba = 23;
        foreach (var dir in directories)
        {
            dir.Lba = nextLba;
            dir.Size = SectorSize;
            nextLba += 1;
        }

        foreach (var file in ordered)
        {
            file.Lba = nextLba;
            nextLba += SectorCount(file.Size);
        }

        int volumeSectors = nextLba + PadSectors;

        using var stream = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write);

        // System area (sectors 0-15): boot IPL then zero fill.
        byte[] systemArea = new byte[16 * SectorSize];
        if (bootIplPath != null && File.Exists(bootIplPath))
        {
            byte[] ipl = File.ReadAllBytes(bootIplPath);
            Array.Copy(ipl, systemArea, Math.Min(ipl.Length, systemArea.Length));
        }
        stream.Write(systemArea);

        // Sector 16: PVD. Sector 17: terminator. Sector 18: reserved.
        byte[] pathTableL = BuildPathTable(directories, littleEndian: true);
        byte[] pathTableM = BuildPathTable(directories, littleEndian: false);
        stream.Write(BuildPvd(volumeId, root, volumeSectors, pathTableL.Length));
        stream.Write(BuildTerminator());
        stream.Write(new byte[SectorSize]);

        // Sectors 19-20 and 21-22: path tables, two sectors reserved each.
        stream.Write(PadToSectors(pathTableL, 2));
        stream.Write(PadToSectors(pathTableM, 2));

        // Directory extents.
        foreach (var dir in directories)
            stream.Write(BuildDirectoryExtent(dir, root));

        // File extents.
        foreach (var file in ordered)
        {
            using (var input = File.OpenRead(file.SourcePath!))
                input.CopyTo(stream);

            int slack = (int)(SectorCount(file.Size) * (long)SectorSize - file.Size);
            if (slack > 0)
                stream.Write(new byte[slack]);
        }

        // Tail padding.
        stream.Write(new byte[PadSectors * SectorSize]);
    }

    private static Entry BuildTree(string directory)
    {
        var root = new Entry
        {
            IsDirectory = true,
            Timestamp = Directory.GetLastWriteTime(directory)
        };
        PopulateChildren(root, directory);
        return root;
    }

    private static void PopulateChildren(Entry parent, string directoryPath)
    {
        var children = new List<Entry>();

        foreach (var path in Directory.GetFileSystemEntries(directoryPath))
        {
            bool isDir = Directory.Exists(path);
            var entry = new Entry
            {
                Name = ToIsoName(Path.GetFileName(path), isDir),
                SourcePath = isDir ? null : path,
                Size = isDir ? 0 : new FileInfo(path).Length,
                IsDirectory = isDir,
                Parent = parent,
                Timestamp = isDir ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path)
            };

            if (isDir)
                PopulateChildren(entry, path);

            children.Add(entry);
        }

        parent.Children = children.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    // Level-1 identifiers are uppercase d-characters only, 8.3 for files.
    // Directories get 8 characters and no dot.
    private static string ToIsoName(string name, bool isDirectory)
    {
        string Clean(string part, int max)
        {
            var sb = new StringBuilder();
            foreach (char c in part.ToUpperInvariant())
            {
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')
                    sb.Append(c);
                else
                    sb.Append('_');
                if (sb.Length == max) break;
            }
            return sb.ToString();
        }

        if (isDirectory)
            return Clean(name, 8);

        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name).TrimStart('.');

        string result = Clean(stem, 8);
        if (ext.Length > 0)
            result += "." + Clean(ext, 3);

        return result;
    }

    private static int SectorCount(long bytes) => (int)((bytes + SectorSize - 1) / SectorSize);

    private static byte[] PadToSectors(byte[] data, int sectors)
    {
        byte[] padded = new byte[sectors * SectorSize];
        Array.Copy(data, padded, data.Length);
        return padded;
    }

    private static byte[] BuildPvd(string volumeId, Entry root, int volumeSectors, int pathTableSize)
    {
        byte[] pvd = new byte[SectorSize];

        pvd[0] = 1;                                        // Volume descriptor type
        WriteAscii(pvd, 1, 5, "CD001");
        pvd[6] = 1;                                        // Version
        WriteAsciiPadded(pvd, 8, 32, Constants.IsoSystemId);
        WriteAsciiPadded(pvd, 40, 32, volumeId);
        WriteBothEndian32(pvd, 80, volumeSectors);         // Volume space size
        WriteBothEndian16(pvd, 120, 1);                    // Volume set size
        WriteBothEndian16(pvd, 124, 1);                    // Volume sequence number
        WriteBothEndian16(pvd, 128, SectorSize);           // Logical block size
        WriteBothEndian32(pvd, 132, pathTableSize);        // Path table size
        WriteLe32(pvd, 140, 19);                           // L path table location
        WriteBe32(pvd, 148, 21);                           // M path table location

        // Root directory record (34 bytes at offset 156).
        byte[] rootRecord = BuildDirectoryRecord(root, isSelfOrParent: true, selfName: 0);
        Array.Copy(rootRecord, 0, pvd, 156, rootRecord.Length);

        // Text fields are space-filled per spec.
        FillSpaces(pvd, 190, 128);                         // Volume set identifier
        FillSpaces(pvd, 318, 128);                         // Publisher
        FillSpaces(pvd, 446, 128);                         // Data preparer
        WriteAsciiPadded(pvd, 574, 128, Constants.AppName.ToUpperInvariant() + " MENU BUILDER");
        FillSpaces(pvd, 702, 37);                          // Copyright file
        FillSpaces(pvd, 739, 37);                          // Abstract file
        FillSpaces(pvd, 776, 37);                          // Bibliographic file

        var now = DateTime.Now;
        WriteVolumeTimestamp(pvd, 813, now);               // Creation
        WriteVolumeTimestamp(pvd, 830, now);               // Modification
        FillZeroDigits(pvd, 847);                          // Expiration (unset)
        FillZeroDigits(pvd, 864);                          // Effective (unset)

        pvd[881] = 1;                                      // File structure version

        return pvd;
    }

    private static byte[] BuildTerminator()
    {
        byte[] term = new byte[SectorSize];
        term[0] = 0xFF;
        WriteAscii(term, 1, 5, "CD001");
        term[6] = 1;
        return term;
    }

    private static byte[] BuildPathTable(List<Entry> directories, bool littleEndian)
    {
        using var ms = new MemoryStream();

        foreach (var dir in directories)
        {
            byte[] name = dir.Parent == null
                ? new byte[] { 0 }
                : Encoding.ASCII.GetBytes(dir.Name);

            ms.WriteByte((byte)name.Length);               // Identifier length
            ms.WriteByte(0);                               // Extended attribute length

            byte[] lba = BitConverter.GetBytes(dir.Lba);
            byte[] parent = BitConverter.GetBytes((ushort)(dir.Parent?.PathTableIndex ?? 1));
            if (!littleEndian)
            {
                Array.Reverse(lba);
                Array.Reverse(parent);
            }
            ms.Write(lba);
            ms.Write(parent);
            ms.Write(name);

            if (name.Length % 2 == 1)
                ms.WriteByte(0);                           // Pad to even length
        }

        return ms.ToArray();
    }

    private static byte[] BuildDirectoryExtent(Entry dir, Entry root)
    {
        byte[] sector = new byte[SectorSize];
        int pos = 0;

        void Append(byte[] record)
        {
            if (pos + record.Length > SectorSize)
                throw new InvalidOperationException("Directory extent exceeds one sector; menu data folder has too many files.");
            Array.Copy(record, 0, sector, pos, record.Length);
            pos += record.Length;
        }

        Append(BuildDirectoryRecord(dir, isSelfOrParent: true, selfName: 0));
        Append(BuildDirectoryRecord(dir.Parent ?? dir, isSelfOrParent: true, selfName: 1));

        foreach (var child in dir.Children)
            Append(BuildDirectoryRecord(child, isSelfOrParent: false, selfName: 0));

        return sector;
    }

    private static byte[] BuildDirectoryRecord(Entry entry, bool isSelfOrParent, byte selfName)
    {
        byte[] nameBytes = isSelfOrParent
            ? new[] { selfName }
            : Encoding.ASCII.GetBytes(entry.IsDirectory ? entry.Name : entry.Name + ";1");

        int recordLength = 33 + nameBytes.Length;
        if (nameBytes.Length % 2 == 0)
            recordLength++;                                // Pad byte for even-length identifiers

        byte[] rec = new byte[recordLength];
        rec[0] = (byte)recordLength;
        rec[1] = 0;                                        // Extended attribute length
        WriteBothEndian32(rec, 2, entry.Lba);
        WriteBothEndian32(rec, 10, (int)(entry.IsDirectory ? SectorSize : entry.Size));
        WriteRecordTimestamp(rec, 18, entry.Timestamp);
        rec[25] = (byte)(entry.IsDirectory ? 0x02 : 0x00); // File flags
        rec[26] = 0;                                       // Unit size
        rec[27] = 0;                                       // Interleave gap
        WriteBothEndian16(rec, 28, 1);                     // Volume sequence number
        rec[32] = (byte)nameBytes.Length;
        Array.Copy(nameBytes, 0, rec, 33, nameBytes.Length);

        return rec;
    }

    // --- Field encoding helpers ---

    private static void WriteAscii(byte[] buffer, int offset, int length, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length));
    }

    private static void WriteAsciiPadded(byte[] buffer, int offset, int length, string value)
    {
        FillSpaces(buffer, offset, length);
        WriteAscii(buffer, offset, length, value);
    }

    private static void FillSpaces(byte[] buffer, int offset, int length)
    {
        for (int i = 0; i < length; i++)
            buffer[offset + i] = 0x20;
    }

    private static void WriteLe32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteBe32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteBothEndian32(byte[] buffer, int offset, int value)
    {
        WriteLe32(buffer, offset, value);
        WriteBe32(buffer, offset + 4, value);
    }

    private static void WriteBothEndian16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    // 17-byte "8.4.3.0" digit-string timestamp used in the PVD.
    private static void WriteVolumeTimestamp(byte[] buffer, int offset, DateTime time)
    {
        string text = time.ToString("yyyyMMddHHmmss") + "00";
        WriteAscii(buffer, offset, 16, text);
        buffer[offset + 16] = 0;                           // GMT offset
    }

    private static void FillZeroDigits(byte[] buffer, int offset)
    {
        for (int i = 0; i < 16; i++)
            buffer[offset + i] = (byte)'0';
        buffer[offset + 16] = 0;
    }

    // 7-byte binary timestamp used in directory records.
    private static void WriteRecordTimestamp(byte[] buffer, int offset, DateTime time)
    {
        buffer[offset] = (byte)(time.Year - 1900);
        buffer[offset + 1] = (byte)time.Month;
        buffer[offset + 2] = (byte)time.Day;
        buffer[offset + 3] = (byte)time.Hour;
        buffer[offset + 4] = (byte)time.Minute;
        buffer[offset + 5] = (byte)time.Second;
        buffer[offset + 6] = 0;                            // GMT offset
    }
}
