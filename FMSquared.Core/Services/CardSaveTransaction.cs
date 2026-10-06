using System.Runtime.CompilerServices;
using FMSquared.Core.Models;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("FMSquared.Core.Tests")]

namespace FMSquared.Core.Services;

internal sealed record CardSaveItem([property: JsonRequired] string Id, [property: JsonRequired] string? OriginalName, [property: JsonRequired] string FinalName)
{
    public CardSaveRow? Row { get; set; }
    [JsonRequired]
    public string StageName { get; set; } = "";
    [JsonRequired]
    public bool Ready { get; set; }
    [JsonRequired]
    public bool Placeholder { get; set; }
    [JsonRequired]
    public Dictionary<string, long>? Files { get; set; }
}

internal sealed record CardSaveRow(string Name, string SourcePath, FileFormat Format,
    FileFormat? InnerFormat, ArchiveEntryInfo? ArchiveEntry, List<string> ImageFiles, long Length, bool Placeholder);

internal sealed class CardSaveTransaction
{
    internal const string JournalName = "FmsSave.json";
    internal const string WorkName = "FmsSave";
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private bool deferPersist;
    private readonly string root;
    private readonly Record record;
    private readonly Func<string, string, Task> moveDirectory;

    internal enum Phase { Preparing, Staging, Publishing, Published, Restaging, Restoring, OrderStaging, OrderPublishing, Committing, Cleaning, OrderedPublished }

    internal sealed class Record
    {
        [JsonRequired]
        public int Version { get; set; }
        [JsonRequired]
        public string Id { get; set; } = "";
        [JsonRequired]
        public int Revision { get; set; }
        [JsonRequired]
        public string Ode { get; set; } = "";
        [JsonRequired]
        public Phase Phase { get; set; }
        [JsonRequired]
        public List<CardSaveItem> Items { get; set; } = new();
        [JsonRequired]
        public List<string> OriginalOrder { get; set; } = new();
        [JsonRequired]
        public List<Metadata> Metadata { get; set; } = new();
        [JsonRequired]
        public List<string> Artifacts { get; set; } = new();
        [JsonRequired]
        public Dictionary<string, Dictionary<string, long>?> WorkDirectories { get; set; } = new();
        [JsonRequired]
        public Dictionary<string, Dictionary<string, long>> Discarding { get; set; } = new();
        [JsonRequired]
        public Dictionary<string, Dictionary<string, long>> Retiring { get; set; } = new();
        [JsonRequired]
        public Dictionary<string, string> RetiringHashes { get; set; } = new();
        [JsonRequired]
        public bool MenuWasPresent { get; set; }
        [JsonRequired]
        public Dictionary<string, long>? MenuFiles { get; set; }
    }

    internal sealed class Metadata
    {
        [JsonRequired]
        public string Target { get; set; } = "";
        [JsonRequired]
        public string? Prepared { get; set; }
        [JsonRequired]
        public string? BeforeHash { get; set; }
        [JsonRequired]
        public string? AfterHash { get; set; }
    }

    private CardSaveTransaction(string root, Record record, Func<string, string, Task>? moveDirectory)
    {
        this.root = Path.GetFullPath(root);
        this.record = record;
        this.moveDirectory = moveDirectory ?? ((from, to) => FolderHelper.MoveDirectoryAsync(from, to));
    }

    internal int InventoryReadCount { get; private set; }
    internal int MetadataHashReadCount { get; private set; }
    internal IReadOnlyList<CardSaveItem> Items => record.Items;
    internal string SelectedOde => record.Ode;
    internal bool HasCommitIntent => record.Phase is Phase.Committing or Phase.Cleaning;

    internal static CardSaveTransaction Begin(string root, string ode, IEnumerable<CardSaveItem> items, Func<string, string, Task>? moveDirectory = null, IEnumerable<string>? removals = null)
    {
        var record = new Record { Version = 1, Id = Guid.NewGuid().ToString("N"), Ode = ode };
        record.Items = items.Select(x => new CardSaveItem(x.Id, x.OriginalName, x.FinalName)
        {
            StageName = "Item-" + Guid.NewGuid().ToString("N"),
            Row = x.Row
        }).ToList();
        var tx = new CardSaveTransaction(root, record, moveDirectory);
        tx.ValidateRecord();
        tx.CheckPath("");
        if (!Directory.Exists(tx.root)) throw new DirectoryNotFoundException(tx.root);
        if (Path.Exists(tx.At(JournalName)) || Path.Exists(tx.At(WorkName)))
            throw new IOException("Reserved save paths are occupied. Recover or review them first.");
        tx.CheckLegacyPaths();
        foreach (string name in removals ?? Array.Empty<string>())
        {
            ValidateName(name);
            if (Reserved(name) || record.Items.Any(x => Names.Equals(x.OriginalName, name)))
                throw new InvalidDataException("Removal overlaps a retained folder: " + name);
            record.Retiring.Add(name, tx.Inventory(name));
        }
        var unknown = Directory.EnumerateDirectories(tx.root).Where(path =>
            int.TryParse(Path.GetFileName(path), out int number) && number != 1 &&
            !record.Items.Any(x => Names.Equals(x.OriginalName, Path.GetFileName(path))) &&
            !record.Retiring.ContainsKey(Path.GetFileName(path))).ToArray();
        if (unknown.Length != 0) throw new InvalidDataException("Unowned numbered folders require review: " + string.Join(", ", unknown));
        foreach (var item in record.Items.Where(x => x.OriginalName != null))
            item.Files = tx.Inventory(item.OriginalName!);
        foreach (var item in record.Items)
        {
            if (Path.Exists(tx.At(item.FinalName)) && !record.Items.Any(x => Names.Equals(x.OriginalName, item.FinalName)) && !record.Retiring.ContainsKey(item.FinalName))
                throw new IOException("Destination is occupied: " + tx.At(item.FinalName));
        }
        record.OriginalOrder = Directory.EnumerateDirectories(tx.root).Select(Path.GetFileName)
            .Where(name => record.Items.Any(x => Names.Equals(x.OriginalName, name))).Cast<string>().ToList();
        record.MenuWasPresent = Directory.Exists(tx.At("01"));
        if (record.MenuWasPresent) record.MenuFiles = tx.Inventory("01");
        else if (Path.Exists(tx.At("01"))) throw new IOException("Menu path is occupied: " + tx.At("01"));
        tx.ValidateRecord();
        tx.WriteInitial();
        Directory.CreateDirectory(tx.At(WorkName));
        return tx;
    }

    internal static CardSaveTransaction? Open(string root, Func<string, string, Task>? moveDirectory = null)
    {
        var probe = new CardSaveTransaction(root, new Record(), moveDirectory);
        probe.CheckPath("");
        probe.CheckPath(JournalName);
        if (!File.Exists(probe.At(JournalName)))
        {
            if (Path.Exists(probe.At(JournalName)) || Path.Exists(probe.At(WorkName)))
                throw new InvalidDataException("Save data has no readable journal: " + probe.At(WorkName));
            probe.CheckLegacyPaths();
            return null;
        }
        Record record;
        try { record = JsonSerializer.Deserialize<Record>(File.ReadAllBytes(probe.At(JournalName)), Json) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new InvalidDataException("Corrupt save journal: " + probe.At(JournalName), ex); }
        var tx = new CardSaveTransaction(root, record, moveDirectory);
        tx.ValidateRecord();
        if (record.OriginalOrder.Count != record.Items.Count(x => x.OriginalName != null) ||
            record.Items.Any(x => (x.OriginalName != null || x.Ready) && x.Files == null))
            throw new InvalidDataException("Incomplete folder ownership records.");
        tx.ValidateWork();
        if (record.Phase == Phase.OrderedPublished) tx.ValidateOrderedPublication();
        return tx;
    }

    internal void Replan(string ode, IEnumerable<CardSaveItem> desired, IReadOnlyCollection<string> removedIds)
    {
        Require(Phase.Preparing);
        ValidateWork();
        FinishRetiring();
        var next = desired.ToList();
        foreach (var old in record.Items)
        {
            var replacement = next.SingleOrDefault(x => Names.Equals(x.Id, old.Id));
            if (replacement == null && !removedIds.Contains(old.Id))
                throw new InvalidDataException("Recorded game is absent without explicit removal: " + old.Id);
            if (replacement != null && (replacement.OriginalName != old.OriginalName ||
                (old.Ready && !SameSource(old.Row, replacement.Row))))
                throw new InvalidDataException("Prepared source selection changed: " + old.Id);
        }
        var planned = next.Select(x =>
        {
            var old = record.Items.SingleOrDefault(o => Names.Equals(o.Id, x.Id));
            return x with
            {
                StageName = old?.StageName ?? "Item-" + Guid.NewGuid().ToString("N"),
                Ready = old?.Ready ?? false,
                Placeholder = old?.Placeholder ?? false,
                Files = old?.Files ?? (x.OriginalName == null ? null : Inventory(x.OriginalName))
            };
        }).ToList();
        foreach (var item in record.Items)
            if (item.OriginalName != null || item.Ready)
                MatchFiles(Inventory(item.OriginalName ?? Stage(item)), item.Files!, item.Id);
        foreach (var action in record.Metadata.Where(x => x.Prepared != null))
            if (!Names.Equals(Hash(action.Prepared!), action.AfterHash))
                throw new InvalidDataException("Prepared metadata changed: " + action.Prepared);
        if (record.Artifacts.Any(x => !record.Metadata.Any(m => Names.Equals(m.Prepared, x))))
            throw new InvalidOperationException("Confirm incomplete metadata cleanup before replanning.");
        var retiringHashes = record.Metadata.Where(x => x.Prepared != null).ToDictionary(x => x.Prepared!, x => x.AfterHash!, Names);
        var retiring = new Dictionary<string, Dictionary<string, long>>(Names);
        foreach (var artifact in record.Artifacts)
            retiring.Add(artifact, PreparationInventory(artifact));
        foreach (var work in record.WorkDirectories)
        {
            if (work.Value == null) throw new InvalidOperationException("Confirm incomplete work cleanup before replanning.");
            retiring.Add(work.Key, work.Value);
        }
        foreach (var old in record.Items.Where(x => !planned.Any(n => Names.Equals(n.Id, x.Id))))
        {
            string path = old.OriginalName ?? Stage(old);
            retiring.Add(path, old.Files ?? PreparationInventory(path));
        }
        var candidate = new Record
        {
            Version = record.Version,
            Id = record.Id,
            Revision = record.Revision,
            Ode = ode,
            Items = planned,
            OriginalOrder = Directory.EnumerateDirectories(root).Select(Path.GetFileName).Where(x => planned.Any(n => Names.Equals(n.OriginalName, x))).Cast<string>().ToList(),
            MenuWasPresent = record.MenuWasPresent,
            MenuFiles = record.MenuFiles,
            Retiring = retiring,
            RetiringHashes = retiringHashes
        };
        new CardSaveTransaction(root, candidate, moveDirectory).ValidateRecord();
        record.Ode = ode;
        record.Items = planned;
        record.OriginalOrder = candidate.OriginalOrder;
        record.Metadata.Clear();
        record.Artifacts.Clear();
        record.WorkDirectories.Clear();
        record.Retiring = retiring;
        record.RetiringHashes = retiringHashes;
        Persist();
        FinishRetiring();
    }

    private static bool SameSource(CardSaveRow? a, CardSaveRow? b) => a == null || b == null ? a == b :
        a.SourcePath == b.SourcePath && a.Format == b.Format && a.InnerFormat == b.InnerFormat &&
        a.Placeholder == b.Placeholder && a.ImageFiles.SequenceEqual(b.ImageFiles) &&
        a.ArchiveEntry?.Ordinal == b.ArchiveEntry?.Ordinal && a.ArchiveEntry?.FullName == b.ArchiveEntry?.FullName &&
        a.ArchiveEntry?.Size == b.ArchiveEntry?.Size;

    private void FinishRetiring()
    {
        if (record.Retiring.Count == 0) return;
        foreach (var entry in record.Retiring)
        {
            CheckPath(entry.Key);
            if (entry.Key.StartsWith(WorkName + "/File-", StringComparison.Ordinal))
            {
                if (File.Exists(At(entry.Key)))
                {
                    if (entry.Value.Count != 1 || entry.Value[""] != new FileInfo(At(entry.Key)).Length ||
                        !record.RetiringHashes.TryGetValue(entry.Key, out string? hash) || !Names.Equals(hash, Hash(entry.Key)))
                        throw new InvalidDataException("Retired metadata changed: " + entry.Key);
                    File.Delete(At(entry.Key));
                }
            }
            else DeleteRecordedTree(entry.Key, entry.Value);
        }
        record.Retiring.Clear();
        record.RetiringHashes.Clear();
        Persist();
    }

    internal string GetImportPath(string id)
    {
        var item = Item(id);
        if (item.OriginalName != null) throw new InvalidOperationException("An existing game is not an import.");
        if (record.Discarding.Count != 0) throw new InvalidOperationException("Recover an interrupted discard first.");
        if (record.Phase != Phase.Preparing) throw new InvalidOperationException("Recover before preparing imports.");
        string path = Stage(item);
        CheckPath(path);
        Directory.CreateDirectory(At(path));
        return At(path);
    }

    internal void MarkReady(string id, IReadOnlyDictionary<string, long> expectedFiles, bool placeholder = false, bool requireSub = true)
    {
        Require(Phase.Preparing);
        var item = Item(id);
        if (item.OriginalName != null || item.Ready) throw new InvalidOperationException("Import cannot be prepared again.");
        var files = new Dictionary<string, long>(expectedFiles, Names);
        ValidateFiles(files);
        var actual = Inventory(Stage(item));
        MatchFiles(actual, files, Stage(item));
        if (!files.ContainsKey("Title.txt"))
            throw new InvalidDataException("A prepared game requires Title.txt.");
        if (placeholder)
        {
            if (files.Count != 1) throw new InvalidDataException("A floppy placeholder contains only Title.txt.");
        }
        else
        {
            if (!files.Any(x => Constants.DiscImageExtensions.Contains(Path.GetExtension(x.Key).ToLowerInvariant()) && x.Value > 0))
                throw new InvalidDataException("A prepared game has no complete disc image.");
            foreach (string file in files.Keys)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".ccd")
                {
                    RequireCompanion(files, file, ".img");
                    if (requireSub) RequireCompanion(files, file, ".sub");
                }
                if (ext == ".mds") RequireCompanion(files, file, ".mdf");
                if (ext is ".cue" or ".chd") throw new InvalidDataException("Import conversion is incomplete: " + file);
            }
        }
        item.Files = files;
        item.Placeholder = placeholder;
        item.Ready = true;
        Persist();
    }

    internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> GetIncompletePreparations()
    {
        Require(Phase.Preparing);
        ValidateWork();
        var paths = record.Items.Where(x => x.OriginalName == null && !x.Ready).Select(Stage)
            .Concat(record.WorkDirectories.Where(x => x.Value == null).Select(x => x.Key))
            .Concat(record.Artifacts.Where(x => !record.Metadata.Any(m => Names.Equals(m.Prepared, x))));
        return paths.ToDictionary(At, x => (IReadOnlyDictionary<string, long>)PreparationInventory(x), Names);
    }

    internal void DiscardIncompletePreparation(string path, IReadOnlyDictionary<string, long> confirmedFiles)
    {
        Require(Phase.Preparing);
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (!IsIncomplete(relative)) throw new InvalidOperationException("Only recorded incomplete preparation can be discarded.");
        var files = new Dictionary<string, long>(confirmedFiles, Names);
        MatchFiles(PreparationInventory(relative), files, relative);
        record.Discarding[relative] = files;
        Persist();
        FinishDiscards();
    }

    private bool IsIncomplete(string path) => record.Items.Any(x => Stage(x) == path && x.OriginalName == null && !x.Ready) ||
        (record.WorkDirectories.TryGetValue(path, out var files) && files == null) ||
        (record.Artifacts.Contains(path, Names) && !record.Metadata.Any(x => Names.Equals(x.Prepared, path)));

    private Dictionary<string, long> PreparationInventory(string path)
    {
        CheckPath(path);
        if (!Path.Exists(At(path))) return new Dictionary<string, long>(Names);
        return record.Artifacts.Contains(path, Names)
            ? new Dictionary<string, long> { [""] = new FileInfo(At(path)).Length }
            : Inventory(path);
    }

    internal string GetWorkDirectory(string id)
    {
        Require(Phase.Preparing);
        ValidateName(id);
        string path = WorkName + "/Work-" + id;
        CheckPath(path);
        if (!record.WorkDirectories.ContainsKey(path))
        {
            if (Path.Exists(At(path))) throw new InvalidDataException("Work path is occupied: " + At(path));
            record.WorkDirectories.Add(path, null);
            Persist();
        }
        Directory.CreateDirectory(At(path));
        return At(path);
    }

    internal void SealWorkDirectory(string id)
    {
        Require(Phase.Preparing);
        ValidateName(id);
        string path = WorkName + "/Work-" + id;
        if (!record.WorkDirectories.TryGetValue(path, out var files) || files != null)
            throw new InvalidOperationException("Work directory is not awaiting completion.");
        record.WorkDirectories[path] = Inventory(path);
        Persist();
    }

    internal void DiscardIncompleteImport(string id, IReadOnlyDictionary<string, long> confirmedFiles)
    {
        var item = Item(id);
        if (item.OriginalName != null || item.Ready) throw new InvalidOperationException("Only incomplete imports can be discarded.");
        DiscardIncompletePreparation(At(Stage(item)), confirmedFiles);
    }

    private void FinishDiscards()
    {
        if (record.Discarding.Count == 0) return;
        foreach (var discard in record.Discarding)
        {
            if (record.Artifacts.Contains(discard.Key, Names))
            {
                CheckPath(discard.Key);
                if (Path.Exists(At(discard.Key)))
                {
                    MatchFiles(PreparationInventory(discard.Key), discard.Value, discard.Key);
                    File.Delete(At(discard.Key));
                }
                record.Artifacts.Remove(discard.Key);
            }
            else
            {
                DeleteRecordedTree(discard.Key, discard.Value);
                record.WorkDirectories.Remove(discard.Key);
            }
        }
        record.Discarding.Clear();
        Persist();
    }

    private void DeleteRecordedTree(string path, Dictionary<string, long> files)
    {
        CheckPath(path);
        if (!Directory.Exists(At(path))) return;
        var actual = Inventory(path);
        if (actual.Any(x => !files.TryGetValue(x.Key, out long size) || size != x.Value))
            throw new InvalidDataException("Unrecorded contents prevent cleanup: " + At(path));
        foreach (string name in actual.Keys.Where(x => !x.EndsWith('/'))) File.Delete(At(path + "/" + name));
        foreach (string name in actual.Keys.Where(x => x.EndsWith('/')).OrderByDescending(x => x.Length)) Directory.Delete(At(path + "/" + name));
        Directory.Delete(At(path));
    }

    internal IReadOnlyList<string> AllocateMetadata(IReadOnlyList<string> ids)
    {
        deferPersist = true;
        List<string> paths;
        try { paths = ids.Select(GetMetadataPath).ToList(); }
        finally { deferPersist = false; }
        Persist();
        return paths;
    }

    internal void RecordMetadataBatch(IEnumerable<(string Target, string? Prepared)> actions, IReadOnlyList<string> unused)
    {
        deferPersist = true;
        try
        {
            foreach (string path in unused)
                record.Artifacts.Remove(Path.GetRelativePath(root, path).Replace('\\', '/'));
            foreach (var action in actions) RecordMetadata(action.Target, action.Prepared);
        }
        finally { deferPersist = false; }
        Persist();
    }

    internal string GetMetadataPath(string id)
    {
        Require(Phase.Preparing);
        ValidateName(id);
        string path = WorkName + "/File-" + id;
        if (record.Artifacts.Contains(path, Names)) throw new InvalidOperationException("Metadata artifact already exists: " + id);
        if (CheckPath(path) != null) throw new IOException("Metadata path is occupied: " + At(path));
        record.Artifacts.Add(path);
        Persist();
        return At(path);
    }

    internal void RecordMetadata(string target, string? prepared, string? originalTarget = null)
    {
        Require(Phase.Preparing);
        ValidateTarget(target);
        if (record.Metadata.Any(x => Names.Equals(x.Target, target))) throw new InvalidDataException("Duplicate metadata target: " + target);
        string? relative = prepared == null ? null : Path.GetRelativePath(root, prepared).Replace('\\', '/');
        if (relative != null && !record.Artifacts.Contains(relative, Names)) throw new InvalidDataException("Unowned metadata source: " + prepared);
        if (relative != null && record.Metadata.Any(x => Names.Equals(x.Prepared, relative))) throw new InvalidDataException("Metadata source is already used.");
        var game = record.Items.SingleOrDefault(x => Names.Equals(target, x.FinalName + "/" + Constants.TitleFile));
        string expectedBefore = game == null ? target : (game.OriginalName ?? Stage(game)) + "/" + Constants.TitleFile;
        string before = originalTarget ?? expectedBefore;
        ValidateRelative(before);
        if (!Names.Equals(before, expectedBefore)) throw new InvalidDataException("Metadata original does not match the folder mapping: " + before);
        string? beforeHash = Hash(before);
        string? afterHash = relative == null ? null : Hash(relative);
        if (relative != null && afterHash == null) throw new FileNotFoundException("Prepared metadata is missing.", prepared);
        record.Metadata.Add(new Metadata { Target = target, Prepared = relative, BeforeHash = beforeHash, AfterHash = afterHash });
        Persist();
    }

    internal async Task PublishAsync()
    {
        Require(Phase.Preparing);
        ValidateWork();
        FinishRetiring();
        if (record.WorkDirectories.Any(x => x.Value == null) || record.Discarding.Count != 0)
            throw new InvalidOperationException("Work preparation or cleanup is incomplete.");
        if (record.Items.Any(x => x.OriginalName == null && !x.Ready)) throw new InvalidOperationException("Some imports are not READY.");
        if (record.Artifacts.Any(x => !record.Metadata.Any(m => Names.Equals(m.Prepared, x))))
            throw new InvalidOperationException("Some metadata is not prepared.");
        foreach (var item in record.Items)
        {
            MatchFiles(Inventory(item.OriginalName ?? Stage(item)), item.Files!, item.Id);
            if (item.OriginalName != null && CheckPath(Stage(item)) != null)
                throw new InvalidDataException("Original and staging paths are both occupied: " + item.Id);
        }
        ValidateNumericFolders(record.Items.Where(x => x.OriginalName != null).Select(x => x.OriginalName!));
        if (record.MenuWasPresent && record.Items.All(x => Names.Equals(x.OriginalName, x.FinalName)) &&
            record.OriginalOrder.SequenceEqual(record.Items.Select(x => x.FinalName), Names) && FinalOrderMatches())
        {
            SetPhase(Phase.OrderedPublished);
            return;
        }
        CheckPath("01");
        Directory.CreateDirectory(At("01"));
        SetPhase(Phase.Staging);
        foreach (var item in record.Items.Where(x => x.OriginalName != null)) await MoveAsync(item.OriginalName!, Stage(item), item.Files!, validateContents: false);
        SetPhase(Phase.Publishing);
        foreach (var item in record.Items) await MoveAsync(Stage(item), item.FinalName, item.Files!, validateContents: false);
        await VerifyFinalOrderAsync();
        SetPhase(Phase.Published);
    }

    internal async Task RecoverAsync()
    {
        ValidateWork();
        if (HasCommitIntent) { await FinishCommitAsync(); return; }
        if (record.Phase == Phase.Preparing)
        {
            foreach (var item in record.Items.Where(x => x.Ready)) MatchFiles(Inventory(Stage(item)), item.Files!, item.Id);
            FinishDiscards();
            FinishRetiring();
            ValidateNumericFolders(record.OriginalOrder);
            return;
        }
        if (record.Phase == Phase.OrderedPublished)
        {
            ValidateOrderedPublication();
            SetPhase(Phase.Preparing);
            return;
        }
        if (record.Phase == Phase.Staging)
        {
            foreach (var item in record.Items.Where(x => x.OriginalName != null)) await MoveAsync(item.OriginalName!, Stage(item), item.Files!);
            SetPhase(Phase.Restoring);
        }
        if (record.Phase is Phase.OrderStaging or Phase.OrderPublishing)
        {
            await MoveAsync("01", WorkName + "/Menu", record.MenuFiles ?? new());
            foreach (var item in record.Items) await MoveAsync(item.FinalName, Stage(item), item.Files!);
            SetPhase(Phase.Restoring);
        }
        if (record.Phase is Phase.Publishing or Phase.Published or Phase.Restaging)
        {
            if (record.Phase != Phase.Restaging) SetPhase(Phase.Restaging);
            foreach (var item in record.Items) await MoveAsync(item.FinalName, Stage(item), item.Files!);
            SetPhase(Phase.Restoring);
        }
        if (record.Phase == Phase.Restoring)
        {
            if (Directory.Exists(At(WorkName + "/Menu"))) await MoveAsync(WorkName + "/Menu", "01", record.MenuFiles ?? new());
            foreach (string original in record.OriginalOrder)
            {
                var item = record.Items.Single(x => Names.Equals(x.OriginalName, original));
                await MoveAsync(Stage(item), original, item.Files!);
            }
            var actual = Directory.EnumerateDirectories(root).Select(Path.GetFileName).Where(x => record.OriginalOrder.Contains(x!, Names));
            if (!actual.SequenceEqual(record.OriginalOrder, Names)) throw new IOException("Original folder order could not be restored.");
            SetPhase(Phase.Preparing);
        }
    }

    internal void RecordCommitIntent() => PrepareCommit();

    private IReadOnlyList<Metadata> PrepareCommit()
    {
        if (record.Phase != Phase.OrderedPublished) Require(Phase.Published);
        ValidateWork();
        if (!FinalOrderMatches()) throw new IOException("Folder order changed before commit.");
        foreach (var item in record.Items)
        {
            MatchFiles(Inventory(item.FinalName), item.Files!, item.Id);
            if (record.Phase == Phase.OrderedPublished && CheckPath(Stage(item)) != null)
                throw new InvalidDataException("Original and staging paths are both occupied: " + item.Id);
        }
        var actions = ValidateMetadata();
        SetPhase(Phase.Committing);
        return actions;
    }

    internal Task CommitAsync()
    {
        var actions = HasCommitIntent ? null : PrepareCommit();
        return FinishCommitAsync(actions);
    }

    private Task FinishCommitAsync(IReadOnlyList<Metadata>? validatedActions = null)
    {
        IReadOnlyList<Metadata> actions;
        if (validatedActions == null)
        {
            ValidateWork();
            if (!FinalOrderMatches()) throw new IOException("Published folder order changed. Preserve the journal for review.");
            actions = ValidateMetadata();
            ValidatePublishedFiles();
        }
        else actions = validatedActions;
        if (record.Phase == Phase.Committing)
        {
            foreach (var action in actions)
            {
                if (action.Prepared == null) File.Delete(At(action.Target));
                else
                {
                    CheckPath(action.Target);
                    Directory.CreateDirectory(Path.GetDirectoryName(At(action.Target))!);
                    File.Move(At(action.Prepared), At(action.Target), overwrite: true);
                }
            }
            // Only recorded metadata changes between game validation and this check.
            if (ValidateMetadata().Count != 0) throw new InvalidDataException("Metadata commit is incomplete.");
            SetPhase(Phase.Cleaning);
        }
        ValidateWork();
        foreach (var work in record.WorkDirectories) DeleteRecordedTree(work.Key, work.Value!);
        if (File.Exists(At(WorkName + "/Journal.next"))) File.Delete(At(WorkName + "/Journal.next"));
        if (Directory.Exists(At(WorkName))) Directory.Delete(At(WorkName), recursive: false);
        File.Delete(At(JournalName));
        return Task.CompletedTask;
    }

    private void ValidatePublishedFiles()
    {
        foreach (var item in record.Items)
        {
            var expected = new Dictionary<string, long>(item.Files!, Names);
            foreach (var action in record.Metadata.Where(x => x.Target.StartsWith(item.FinalName + "/", StringComparison.OrdinalIgnoreCase)))
            {
                string name = action.Target[(item.FinalName.Length + 1)..];
                if (File.Exists(At(action.Target)))
                {
                    expected[name] = new FileInfo(At(action.Target)).Length;
                    for (string? parent = Path.GetDirectoryName(name); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                        expected[parent.Replace('\\', '/') + "/"] = -1;
                }
                else expected.Remove(name);
            }
            MatchFiles(Inventory(item.FinalName), expected, item.FinalName);
        }
    }

    private IReadOnlyList<Metadata> ValidateMetadata()
    {
        var pending = new List<Metadata>();
        foreach (var action in record.Metadata)
        {
            string? current = Hash(action.Target);
            bool preparedExists = action.Prepared != null && File.Exists(At(action.Prepared));
            if (record.Phase == Phase.Cleaning && (!Names.Equals(current, action.AfterHash) || preparedExists))
                throw new InvalidDataException("Committed metadata changed: " + At(action.Target));
            if (preparedExists)
            {
                if (!Names.Equals(Hash(action.Prepared!), action.AfterHash) || !Names.Equals(current, action.BeforeHash))
                    throw new InvalidDataException("Metadata changed: " + At(action.Target));
                pending.Add(action);
            }
            else if (!Names.Equals(current, action.AfterHash))
            {
                if (action.Prepared != null || !Names.Equals(current, action.BeforeHash))
                    throw new InvalidDataException("Metadata cannot be recovered: " + At(action.Target));
                pending.Add(action);
            }
        }
        return pending;
    }

    private async Task VerifyFinalOrderAsync()
    {
        for (int attempt = 0; !FinalOrderMatches(); attempt++)
        {
            if (attempt == 2) throw new IOException("The card did not retain the requested directory order.");
            SetPhase(Phase.OrderStaging);
            await MoveAsync("01", WorkName + "/Menu", record.MenuFiles ?? new());
            foreach (var item in record.Items) await MoveAsync(item.FinalName, Stage(item), item.Files!);
            SetPhase(Phase.OrderPublishing);
            await MoveAsync(WorkName + "/Menu", "01", record.MenuFiles ?? new());
            foreach (var item in record.Items) await MoveAsync(Stage(item), item.FinalName, item.Files!);
        }
    }

    private bool FinalOrderMatches()
    {
        CheckPath("01");
        var expected = new[] { "01" }.Concat(record.Items.Select(x => x.FinalName)).ToArray();
        ValidateNumericFolders(record.Items.Select(x => x.FinalName));
        var actual = Directory.EnumerateDirectories(root).Select(Path.GetFileName).Where(x => int.TryParse(x, out _));
        return actual.SequenceEqual(expected, Names);
    }

    private void ValidateNumericFolders(IEnumerable<string> expected)
    {
        var actual = Directory.EnumerateDirectories(root).Select(Path.GetFileName)
            .Where(x => int.TryParse(x, out _) && !Names.Equals(x, "01"));
        if (!new HashSet<string>(actual!, Names).SetEquals(expected.Where(x => int.TryParse(x, out _))))
            throw new InvalidDataException("Numbered folders no longer match the recorded mapping.");
    }

    private void ValidateOrderedPublication()
    {
        if (!FinalOrderMatches()) throw new IOException("Card order changed during save.");
        foreach (var item in record.Items)
        {
            MatchFiles(Inventory(item.OriginalName!), item.Files!, item.Id);
            if (CheckPath(Stage(item)) != null)
                throw new InvalidDataException("Original and staging paths are both occupied: " + item.Id);
        }
    }

    private async Task MoveAsync(string source, string destination, Dictionary<string, long> files, bool validateContents = true)
    {
        var sourceStatus = CheckPath(source);
        var destinationStatus = CheckPath(destination);
        bool from = sourceStatus != null && (sourceStatus & FileAttributes.Directory) != 0;
        bool to = destinationStatus != null && (destinationStatus & FileAttributes.Directory) != 0;
        if (from == to || (sourceStatus != null && !from) || (destinationStatus != null && !to))
            throw new InvalidDataException("Ambiguous or missing folder mapping: " + At(source) + " -> " + At(destination));
        if (validateContents) MatchFiles(Inventory(from ? source : destination), files, from ? source : destination);
        if (from) await moveDirectory(At(source), At(destination));
    }

    private void ValidateRecord()
    {
        if (record.Version != 1 || !Guid.TryParseExact(record.Id, "N", out _) || record.Revision < 0 ||
            record.Ode is not ("Wizard" or "DocBrown" or "Unknown") || !Enum.IsDefined(record.Phase) ||
            record.Items == null || record.OriginalOrder == null || record.Metadata == null || record.Artifacts == null || record.WorkDirectories == null || record.Discarding == null || record.Retiring == null || record.RetiringHashes == null)
            throw new InvalidDataException("Unsupported or incomplete save record.");
        if (record.Phase != Phase.Preparing && (record.Items.Any(x => x.OriginalName == null && !x.Ready) ||
            record.WorkDirectories.Any(x => x.Value == null) || record.Artifacts.Any(x => !record.Metadata.Any(m => Names.Equals(m.Prepared, x)))))
            throw new InvalidDataException("Publication includes unfinished preparation.");
        foreach (var retired in record.Retiring)
        {
            ValidateRelative(retired.Key);
            bool work = new[] { "File-", "Work-", "Item-" }.Any(prefix => retired.Key.StartsWith(WorkName + "/" + prefix, StringComparison.Ordinal)) &&
                retired.Key.Count(c => c == '/') == 1;
            if (record.Phase != Phase.Preparing || (!work && (retired.Key.Contains('/') || Reserved(retired.Key))) ||
                record.Items.Any(x => Names.Equals(x.OriginalName, retired.Key) || Names.Equals(Stage(x), retired.Key)))
                throw new InvalidDataException("Invalid retired save data: " + retired.Key);
            if (retired.Key.StartsWith(WorkName + "/File-", StringComparison.Ordinal))
            {
                if (retired.Value.Count > 1 || (retired.Value.Count == 1 && (!retired.Value.TryGetValue("", out long n) || n < 0)))
                    throw new InvalidDataException("Invalid retired metadata.");
            }
            else ValidateFiles(retired.Value);
        }
        foreach (var hash in record.RetiringHashes)
            if (!record.Retiring.ContainsKey(hash.Key) || !hash.Key.StartsWith(WorkName + "/File-", StringComparison.Ordinal) || !ValidHash(hash.Value))
                throw new InvalidDataException("Invalid retired metadata hash.");
        var ids = new HashSet<string>(Names);
        var originals = new HashSet<string>(Names);
        var finals = new HashSet<string>(Names);
        var stages = new HashSet<string>(Names);
        foreach (var item in record.Items)
        {
            if (item == null) throw new InvalidDataException("Missing item record.");
            ValidateName(item.Id);
            ValidateName(item.FinalName);
            if (!int.TryParse(item.FinalName, out int number) || number < 2 || number.ToString("D2") != item.FinalName)
                throw new InvalidDataException("Invalid final folder: " + item.FinalName);
            if (item.StageName == null || !item.StageName.StartsWith("Item-", StringComparison.Ordinal) || !Guid.TryParseExact(item.StageName[5..], "N", out _))
                throw new InvalidDataException("Invalid staging name.");
            if (!ids.Add(item.Id) || !finals.Add(item.FinalName) || !stages.Add(item.StageName)) throw new InvalidDataException("Duplicate item mapping.");
            if (item.OriginalName != null)
            {
                ValidateName(item.OriginalName);
                if (Reserved(item.OriginalName) || !originals.Add(item.OriginalName)) throw new InvalidDataException("Invalid original folder: " + item.OriginalName);
            }
            if (item.Row is { } row && (row.Name == null || row.SourcePath == null || row.ImageFiles == null ||
                row.ImageFiles.Any(string.IsNullOrWhiteSpace) || !Enum.IsDefined(row.Format) ||
                (row.InnerFormat != null && !Enum.IsDefined(row.InnerFormat.Value))))
                throw new InvalidDataException("Invalid source row: " + item.Id);
            if (item.Files != null) ValidateFiles(item.Files);
            if (item.Ready && (item.OriginalName != null || item.Files == null)) throw new InvalidDataException("Invalid READY record.");
        }
        if (record.Phase == Phase.OrderedPublished && (!record.MenuWasPresent ||
            record.Items.Any(x => !Names.Equals(x.OriginalName, x.FinalName)) ||
            !record.OriginalOrder.SequenceEqual(record.Items.Select(x => x.FinalName), Names)))
            throw new InvalidDataException("Invalid ordered publication mapping.");
        if (record.OriginalOrder.Distinct(Names).Count() != record.OriginalOrder.Count || record.OriginalOrder.Any(x => !originals.Contains(x)))
            throw new InvalidDataException("Invalid original folder order.");
        foreach (var work in record.WorkDirectories)
        {
            ValidateRelative(work.Key);
            if (!work.Key.StartsWith(WorkName + "/Work-", StringComparison.Ordinal) || work.Key.Count(x => x == '/') != 1)
                throw new InvalidDataException("Invalid owned work directory.");
            if (work.Value != null) ValidateFiles(work.Value);
        }
        foreach (var discard in record.Discarding)
        {
            if (record.Phase != Phase.Preparing || !IsIncomplete(discard.Key))
                throw new InvalidDataException("Invalid incomplete import discard.");
            if (record.Artifacts.Contains(discard.Key, Names))
            {
                if (discard.Value.Count > 1 || (discard.Value.Count == 1 && (!discard.Value.TryGetValue("", out long size) || size < 0)))
                    throw new InvalidDataException("Invalid incomplete metadata inventory.");
            }
            else ValidateFiles(discard.Value);
        }
        var artifacts = new HashSet<string>(Names);
        foreach (string artifact in record.Artifacts)
        {
            ValidateRelative(artifact);
            if (!artifact.StartsWith(WorkName + "/File-", StringComparison.Ordinal) || artifact.Count(x => x == '/') != 1 || !artifacts.Add(artifact))
                throw new InvalidDataException("Invalid owned metadata path.");
        }
        var targets = new HashSet<string>(Names);
        var prepared = new HashSet<string>(Names);
        foreach (var action in record.Metadata)
        {
            ValidateTarget(action.Target);
            if (!targets.Add(action.Target) || (action.Prepared != null && (!artifacts.Contains(action.Prepared) || !prepared.Add(action.Prepared) || !ValidHash(action.AfterHash))) ||
                (action.BeforeHash != null && !ValidHash(action.BeforeHash)) || (action.Prepared == null && action.AfterHash != null))
                throw new InvalidDataException("Invalid metadata record.");
        }
    }

    private void ValidateWork()
    {
        CheckPath(WorkName);
        if (!Directory.Exists(At(WorkName)))
        {
            if (Path.Exists(At(WorkName)) || record.Phase is not (Phase.Preparing or Phase.Cleaning)) throw new InvalidDataException("Save workspace is missing.");
            return;
        }
        foreach (string path in Directory.EnumerateFileSystemEntries(At(WorkName)))
        {
            string name = Path.GetFileName(path);
            CheckPath(WorkName + "/" + name);
            bool owned = record.Retiring.ContainsKey(WorkName + "/" + name) || record.Items.Any(x => Names.Equals(x.StageName, name)) || record.Artifacts.Contains(WorkName + "/" + name, Names) || record.WorkDirectories.ContainsKey(WorkName + "/" + name) ||
                (name == "Menu" && record.Phase is Phase.OrderStaging or Phase.OrderPublishing or Phase.Restoring);
            if (name == "Journal.next")
            {
                Record? next;
                try { next = JsonSerializer.Deserialize<Record>(File.ReadAllBytes(path), Json); }
                catch (JsonException ex) { throw new InvalidDataException("Unverified journal write: " + path, ex); }
                if (next?.Id != record.Id || next.Revision != record.Revision + 1) throw new InvalidDataException("Unowned journal write: " + path);
                continue;
            }
            if (!owned) throw new InvalidDataException("Unowned save data: " + path);
            if (record.WorkDirectories.TryGetValue(WorkName + "/" + name, out var workFiles) && workFiles != null)
            {
                var actual = Inventory(WorkName + "/" + name);
                if (record.Phase == Phase.Cleaning)
                {
                    if (actual.Any(x => !workFiles.TryGetValue(x.Key, out long size) || size != x.Value))
                        throw new InvalidDataException("Work directory changed: " + path);
                }
                else MatchFiles(actual, workFiles, path);
            }
        }
    }

    private void Persist()
    {
        if (deferPersist) return;
        ValidateWork();
        Directory.CreateDirectory(At(WorkName));
        string next = At(WorkName + "/Journal.next");
        if (File.Exists(next)) File.Delete(next);
        int revision = record.Revision;
        try
        {
            record.Revision++;
            using (var stream = new FileStream(next, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(next, At(JournalName), overwrite: true);
        }
        catch
        {
            record.Revision = revision;
            throw;
        }
    }

    private void WriteInitial()
    {
        using var stream = new FileStream(At(JournalName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, record, Json);
        stream.Flush(flushToDisk: true);
    }

    private void SetPhase(Phase phase)
    {
        Phase previous = record.Phase;
        record.Phase = phase;
        try { Persist(); }
        catch { record.Phase = previous; throw; }
    }
    private void Require(Phase phase) { if (record.Phase != phase) throw new InvalidOperationException("Save is in phase " + record.Phase + "."); }
    private CardSaveItem Item(string id) => record.Items.Single(x => Names.Equals(x.Id, id));
    private string Stage(CardSaveItem item) => WorkName + "/" + item.StageName;
    private string At(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private Dictionary<string, long> Inventory(string relative)
    {
        InventoryReadCount++;
        var status = CheckPath(relative);
        if (status == null || (status & FileAttributes.Directory) == 0) throw new InvalidDataException("Game folder is missing: " + At(relative));
        var files = new Dictionary<string, long>(Names);
        void Read(string directory, string prefix)
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked paths are not supported: " + entry.FullName);
                string name = prefix + entry.Name;
                if (entry is DirectoryInfo)
                {
                    files.Add(name + "/", -1);
                    Read(entry.FullName, name + "/");
                }
                else files.Add(name, ((FileInfo)entry).Length);
            }
        }
        Read(At(relative), "");
        return files;
    }

    private static void MatchFiles(Dictionary<string, long> actual, Dictionary<string, long> expected, string path)
    {
        if (actual.Count != expected.Count || expected.Any(x => !actual.TryGetValue(x.Key, out long size) || size != x.Value))
            throw new InvalidDataException("Folder contents changed or are incomplete: " + path);
    }

    private string? Hash(string relative)
    {
        var status = CheckPath(relative);
        if (status == null) return null;
        if ((status & FileAttributes.Directory) != 0) throw new InvalidDataException("Expected a file: " + At(relative));
        MetadataHashReadCount++;
        using var stream = File.OpenRead(At(relative));
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool ValidHash(string? hash) => hash?.Length == 64 && hash.All(Uri.IsHexDigit);
    private static void RequireCompanion(Dictionary<string, long> files, string file, string extension)
    {
        if (!files.TryGetValue(Path.ChangeExtension(file, extension), out long size) || size <= 0)
            throw new InvalidDataException("Missing image companion: " + Path.ChangeExtension(file, extension));
    }

    private FileAttributes? CheckPath(string relative)
    {
        string path = root;
        var status = Check(path);
        foreach (string part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (status != null && (status & FileAttributes.Directory) == 0)
                throw new IOException("Expected a directory: " + path);
            path = Path.Combine(path, part);
            status = Check(path);
        }
        return status;

        static FileAttributes? Check(string path)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked paths are not supported: " + path);
            return attributes;
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(x => x < 32 || "<>:\"/\\|?*".Contains(x))) throw new InvalidDataException("Invalid card path component: " + name);
    }

    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new InvalidDataException("Missing card path.");
        foreach (string part in path.Split('/')) ValidateName(part);
    }

    private void ValidateTarget(string target)
    {
        ValidateRelative(target);
        string[] parts = target.Split('/');
        bool allowed = (parts.Length == 1 && new[] { Constants.GameListFile, Constants.DocBrownIniFile, Constants.WizardIniFile }.Contains(target, Names)) ||
            (parts.Length > 1 && Names.Equals(parts[0], "01")) ||
            (parts.Length == 2 && Names.Equals(parts[1], Constants.TitleFile) && record.Items.Any(x => Names.Equals(x.FinalName, parts[0])));
        if (!allowed) throw new InvalidDataException("Not a managed metadata path: " + target);
    }

    private static void ValidateFiles(Dictionary<string, long> files)
    {
        var names = new HashSet<string>(Names);
        foreach (var file in files)
        {
            ValidateRelative(file.Key.TrimEnd('/'));
            if (!names.Add(file.Key) || (file.Key.EndsWith('/') ? file.Value != -1 : file.Value < 0)) throw new InvalidDataException("Invalid file inventory.");
        }
    }

    private static bool Reserved(string name) => Names.Equals(name, "01") || Names.Equals(name, WorkName) || Names.Equals(name, JournalName) ||
        Names.Equals(name, "fmsquared_temp") || Names.Equals(name, "towns_sorter_temp") || name.Contains("_lockcheck_", StringComparison.OrdinalIgnoreCase);

    private void CheckLegacyPaths()
    {
        if (!Directory.Exists(root)) return;
        var paths = Directory.EnumerateFileSystemEntries(root).Where(x =>
        {
            string name = Path.GetFileName(x);
            return Names.Equals(name, "fmsquared_temp") || Names.Equals(name, "towns_sorter_temp") || name.Contains("_lockcheck_", StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        if (paths.Length > 0) throw new InvalidDataException("Unrecorded recovery paths require review: " + string.Join(", ", paths));
    }
}
