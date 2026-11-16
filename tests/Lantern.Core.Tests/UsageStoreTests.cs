using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.Json;
using Lantern.Core.Control;
using Lantern.Core.Devices;
using Lantern.Core.Usage;

namespace Lantern.Core.Tests;

public sealed class UsageStoreTests
{
    private const string Phone = "E261190DBD54";

    [Fact]
    public void Load_OnlyTwoMissingCopiesMeanAnEmptyNewLedger()
    {
        using var temporary = new UsageTestDirectory();
        var missingDirectory = Path.Combine(temporary.DirectoryPath, "not-created");

        Assert.Empty(new UsageStore(missingDirectory).Load());
        Assert.False(Directory.Exists(missingDirectory));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ReadFailureThenMissingPrimaryNeverLooksFreshOrOverwritesHistory(bool directoryMissing, bool save)
    {
        using var temporary = new UsageTestDirectory();
        var original = Document(Totals(123, 45));
        File.WriteAllText(temporary.PrimaryPath, original);
        var readFailure = new IOException("Injected initial read failure.");
        var primaryAttempts = 0;
        var backupAttempts = 0;
        var store = CreateStoreWithReader(temporary.DirectoryPath, path =>
        {
            if (path == temporary.PrimaryPath)
            {
                if (++primaryAttempts == 1)
                {
                    throw readFailure;
                }

                throw MissingReadError(path, directoryMissing);
            }

            Assert.Equal(temporary.BackupPath, path);
            backupAttempts++;
            throw new FileNotFoundException("Injected missing backup.", path);
        });
        var tracker = new UsageTracker(new DeviceRegistry(), store);

        var error = save
            ? Assert.Throws<IOException>(() => store.Save(Totals(1, 0)))
            : Assert.Throws<IOException>(tracker.Load);

        Assert.Contains(readFailure, Assert.IsType<AggregateException>(error.InnerException).InnerExceptions);
        Assert.False(tracker.IsLoaded);
        Assert.False(tracker.FlushAndSave());
        Assert.Equal(2, primaryAttempts);
        Assert.Equal(1, backupAttempts);
        Assert.Equal(original, File.ReadAllText(temporary.PrimaryPath));
        Assert.Equal(temporary.PrimaryPath, Assert.Single(Directory.GetFiles(temporary.DirectoryPath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Load_ReadFailureThenMissingPrimaryCanStillUseAValidBackup(bool directoryMissing)
    {
        using var temporary = new UsageTestDirectory();
        var original = Document(Totals(123, 45));
        File.WriteAllText(temporary.PrimaryPath, original);
        File.WriteAllText(temporary.BackupPath, original);
        var primaryAttempts = 0;
        var store = CreateStoreWithReader(temporary.DirectoryPath, path =>
        {
            if (path == temporary.PrimaryPath)
            {
                if (++primaryAttempts == 1)
                {
                    throw new IOException("Injected initial read failure.");
                }

                throw MissingReadError(path, directoryMissing);
            }

            return File.OpenRead(path);
        });

        Assert.Equal(new DeviceUsage(123, 45), Assert.Single(store.Load()).Value);
        Assert.Equal(2, primaryAttempts);
        Assert.Equal(original, File.ReadAllText(temporary.PrimaryPath));
        Assert.Equal(original, File.ReadAllText(temporary.BackupPath));
    }

    [Fact]
    public void Load_SuccessfulPrimaryRetryCanRecoverFromAnEarlierReadFailure()
    {
        using var temporary = new UsageTestDirectory();
        var original = Document(Totals(123, 45));
        File.WriteAllText(temporary.PrimaryPath, original);
        var primaryAttempts = 0;
        var store = CreateStoreWithReader(temporary.DirectoryPath, path =>
        {
            if (path == temporary.PrimaryPath && ++primaryAttempts == 1)
            {
                throw new IOException("Injected initial read failure.");
            }

            return File.OpenRead(path);
        });

        Assert.Equal(new DeviceUsage(123, 45), Assert.Single(store.Load()).Value);
        Assert.Equal(2, primaryAttempts);
        Assert.Equal(original, File.ReadAllText(temporary.PrimaryPath));
        Assert.False(File.Exists(temporary.BackupPath));
    }

    [Fact]
    public void Load_MissingPrimaryUsesBackupAndTheNextSaveKeepsItsHistory()
    {
        using var temporary = new UsageTestDirectory();
        var store = new UsageStore(temporary.DirectoryPath);
        store.Save(Totals(123, 45));
        File.Delete(temporary.PrimaryPath);

        var registry = new DeviceRegistry();
        var tracker = new UsageTracker(registry, store);
        tracker.Load();
        Assert.Equal(new DeviceUsage(123, 45), registry.Usage.Get(Phone));
        Assert.False(File.Exists(temporary.PrimaryPath));
        registry.Usage.Add(Phone, 7, 3);

        Assert.True(tracker.SaveIfChanged());
        Assert.Equal(new DeviceUsage(130, 48), store.Load()[Phone]);
        Assert.Equal(File.ReadAllText(temporary.PrimaryPath), File.ReadAllText(temporary.BackupPath));
    }

    [Theory]
    [InlineData("{ not json", "[ also broken")]
    [InlineData("null", "null")]
    [InlineData("{}", "{}")]
    [InlineData("{\"schemaVersion\":1}", "{\"schemaVersion\":1,\"devices\":null}")]
    public void Load_TwoCorruptCopiesCannotBecomeAnEmptyLoadedTrackerOrBeOverwritten(
        string primary,
        string backup)
    {
        using var temporary = new UsageTestDirectory();
        File.WriteAllText(temporary.PrimaryPath, primary);
        File.WriteAllText(temporary.BackupPath, backup);
        var store = new UsageStore(temporary.DirectoryPath);
        var registry = new DeviceRegistry();
        var tracker = new UsageTracker(registry, store);
        var phone = PhysicalAddress.Parse(Phone);
        registry.Observe(IPAddress.Parse("192.168.31.61"), phone, DateTimeOffset.UtcNow);
        registry.RecordTraffic(phone, TrafficDirection.Download, 99);

        Assert.Throws<IOException>(tracker.Load);
        Assert.False(tracker.IsLoaded);
        Assert.False(tracker.FlushAndSave());
        Assert.Throws<IOException>(() => store.Save(Totals(99, 0)));
        Assert.Equal(primary, File.ReadAllText(temporary.PrimaryPath));
        Assert.Equal(backup, File.ReadAllText(temporary.BackupPath));
        Assert.Empty(Directory.GetFiles(temporary.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("usage.json")]
    [InlineData("usage.backup.json")]
    public void Load_OneCorruptCopyAndOneMissingCopyStillThrows(string fileName)
    {
        using var temporary = new UsageTestDirectory();
        File.WriteAllText(Path.Combine(temporary.DirectoryPath, fileName), "broken history");

        Assert.Throws<IOException>(() => new UsageStore(temporary.DirectoryPath).Load());
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"devices\":{\"E261190DBD54\":null}}")]
    [InlineData("{\"schemaVersion\":1,\"devices\":{\"not-a-mac\":{\"downloadBytes\":1,\"uploadBytes\":0,\"totalBytes\":1}}}")]
    [InlineData("{\"schemaVersion\":1,\"devices\":{\"E261190DBD54\":{\"downloadBytes\":-1,\"uploadBytes\":0,\"totalBytes\":0}}}")]
    [InlineData("{\"schemaVersion\":1,\"devices\":{\"E261190DBD54\":{\"downloadBytes\":1,\"totalBytes\":1}}}")]
    [InlineData("{\"schemaVersion\":1,\"devices\":{\"E261190DBD54\":{\"downloadBytes\":1,\"uploadBytes\":2,\"totalBytes\":4}}}")]
    [InlineData("{\"schemaVersion\":1,\"devices\":{\"E261190DBD54\":{\"downloadBytes\":9223372036854775807,\"uploadBytes\":1,\"totalBytes\":9223372036854775807}}}")]
    [InlineData("{\"schemaVersion\":1,\"devices\":{\"E261190DBD54\":{\"downloadBytes\":9223372036854775808,\"uploadBytes\":0,\"totalBytes\":0}}}")]
    [InlineData("{\"schemaVersion\":1,\"devices\":{\"E261190DBD54\":{\"downloadBytes\":1,\"downloadBytes\":2,\"uploadBytes\":0,\"totalBytes\":2}}}")]
    [InlineData("{\"schemaVersion\":1,\"devices\":{\"E261190DBD54\":{\"downloadBytes\":1,\"uploadBytes\":0,\"totalBytes\":1},\"e2:61:19:0d:bd:54\":{\"downloadBytes\":2,\"uploadBytes\":0,\"totalBytes\":2}}}")]
    [InlineData("{\"schemaVersion\":1,\"devices\":{},\"devices\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"savedAtUtc\":\"not-a-date\",\"devices\":{}}")]
    [InlineData("{\"schemaVersion\":0,\"devices\":{}}")]
    public void Load_InvalidInvariantsUseTheWholeBackupWithoutRepairingOrInventingTotals(string invalid)
    {
        using var temporary = new UsageTestDirectory();
        var store = new UsageStore(temporary.DirectoryPath);
        store.Save(Totals(123, 45));
        var backup = File.ReadAllText(temporary.BackupPath);
        File.WriteAllText(temporary.PrimaryPath, invalid);

        var loaded = store.Load();

        Assert.Equal(new DeviceUsage(123, 45), Assert.Single(loaded).Value);
        Assert.Equal(invalid, File.ReadAllText(temporary.PrimaryPath));
        Assert.Equal(backup, File.ReadAllText(temporary.BackupPath));
    }

    [Theory]
    [InlineData("usage.json")]
    [InlineData("usage.backup.json")]
    public void Load_FutureSchemaInEitherCopyRefusesFallbackAndSaving(string futureFile)
    {
        using var temporary = new UsageTestDirectory();
        var store = new UsageStore(temporary.DirectoryPath);
        store.Save(Totals(123, 45));
        File.WriteAllText(
            Path.Combine(temporary.DirectoryPath, futureFile),
            "{\"schemaVersion\":2,\"devices\":{}}");
        var primary = File.ReadAllText(temporary.PrimaryPath);
        var backup = File.ReadAllText(temporary.BackupPath);

        Assert.Throws<IOException>(() => store.Load());
        Assert.Throws<IOException>(() => store.Save(Totals(124, 46)));
        Assert.Equal(primary, File.ReadAllText(temporary.PrimaryPath));
        Assert.Equal(backup, File.ReadAllText(temporary.BackupPath));
    }

    [Fact]
    public void Load_UnreadablePrimaryCanStillUseAValidBackup()
    {
        using var temporary = new UsageTestDirectory();
        var store = new UsageStore(temporary.DirectoryPath);
        store.Save(Totals(123, 45));
        using var exclusive = new FileStream(
            temporary.PrimaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(new DeviceUsage(123, 45), store.Load()[Phone]);
    }

    [Theory]
    [InlineData(100, 10)]
    [InlineData(0, 0)]
    public void Load_BackupAheadOfPrimaryUsesThatCompleteSnapshot(long primaryDownload, long primaryUpload)
    {
        using var temporary = new UsageTestDirectory();
        File.WriteAllText(temporary.PrimaryPath, Document(Totals(primaryDownload, primaryUpload)));
        File.WriteAllText(temporary.BackupPath, Document(Totals(110, 20)));

        var loaded = new UsageStore(temporary.DirectoryPath).Load();

        Assert.Equal(new DeviceUsage(110, 20), Assert.Single(loaded).Value);
    }

    [Fact]
    public void Load_EmptyPrimaryDoesNotHideNonemptyBackupHistory()
    {
        using var temporary = new UsageTestDirectory();
        File.WriteAllText(temporary.PrimaryPath, Document(new Dictionary<string, DeviceUsage>()));
        File.WriteAllText(temporary.BackupPath, Document(Totals(110, 20)));

        Assert.Equal(
            new DeviceUsage(110, 20),
            new UsageStore(temporary.DirectoryPath).Load()[Phone]);
    }

    [Fact]
    public void Load_ConflictingValidCopiesAreNotSummedOrMerged()
    {
        using var temporary = new UsageTestDirectory();
        var primary = Document(Totals(100, 10));
        var backup = Document(Totals(90, 20));
        File.WriteAllText(temporary.PrimaryPath, primary);
        File.WriteAllText(temporary.BackupPath, backup);
        var store = new UsageStore(temporary.DirectoryPath);

        Assert.Throws<IOException>(() => store.Load());
        Assert.Throws<IOException>(() => store.Save(Totals(100, 20)));
        Assert.Equal(primary, File.ReadAllText(temporary.PrimaryPath));
        Assert.Equal(backup, File.ReadAllText(temporary.BackupPath));
    }

    [Theory]
    [InlineData(99, 20)]
    [InlineData(100, 19)]
    [InlineData(99, 21)]
    [InlineData(101, 19)]
    public void Save_LowerDirectionalTotalsAreRejectedWithoutTouchingEitherCopy(long download, long upload)
    {
        using var temporary = new UsageTestDirectory();
        var store = new UsageStore(temporary.DirectoryPath);
        store.Save(Totals(100, 20));
        var primary = File.ReadAllText(temporary.PrimaryPath);
        var backup = File.ReadAllText(temporary.BackupPath);

        Assert.Throws<IOException>(() => store.Save(Totals(download, upload)));
        Assert.Throws<IOException>(() => store.Save(new Dictionary<string, DeviceUsage>()));
        Assert.Equal(primary, File.ReadAllText(temporary.PrimaryPath));
        Assert.Equal(backup, File.ReadAllText(temporary.BackupPath));
    }

    [Fact]
    public void Save_InvalidOrDuplicateEntriesCannotPartiallyReplaceValidHistory()
    {
        using var temporary = new UsageTestDirectory();
        var store = new UsageStore(temporary.DirectoryPath);
        store.Save(Totals(100, 20));
        var primary = File.ReadAllText(temporary.PrimaryPath);
        var backup = File.ReadAllText(temporary.BackupPath);
        IReadOnlyDictionary<string, DeviceUsage>[] invalidSnapshots =
        [
            new Dictionary<string, DeviceUsage> { ["not-a-mac"] = new(1, 2) },
            new Dictionary<string, DeviceUsage> { [Phone] = new(-1, 0) },
            new Dictionary<string, DeviceUsage> { [Phone] = new(long.MaxValue, 1) },
            new Dictionary<string, DeviceUsage> { [Phone] = null! },
            new Dictionary<string, DeviceUsage>
            {
                [Phone] = new(100, 20),
                ["e2:61:19:0d:bd:54"] = new(100, 20),
            },
        ];

        foreach (var invalid in invalidSnapshots)
        {
            Assert.Throws<ArgumentException>(() => store.Save(invalid));
        }

        Assert.Equal(primary, File.ReadAllText(temporary.PrimaryPath));
        Assert.Equal(backup, File.ReadAllText(temporary.BackupPath));
        Assert.Empty(Directory.GetFiles(temporary.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task Save_ConcurrentStoresCannotReplaceNewerTotalsWithAStaleSnapshot()
    {
        using var temporary = new UsageTestDirectory();
        var first = new UsageStore(temporary.DirectoryPath);
        var second = new UsageStore(Path.Combine(temporary.DirectoryPath, "."));
        var saves = Enumerable.Range(1, 16).Select(value => Task.Run(() =>
        {
            try
            {
                (value % 2 == 0 ? first : second).Save(Totals(value, value));
            }
            catch (IOException)
            {
                // A snapshot that loses the race to a newer one must be refused.
            }
        }));

        await Task.WhenAll(saves);

        Assert.Equal(new DeviceUsage(16, 16), first.Load()[Phone]);
        Assert.Equal(File.ReadAllText(temporary.PrimaryPath), File.ReadAllText(temporary.BackupPath));
        Assert.Empty(Directory.GetFiles(temporary.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void Tracker_FailedSaveDoesNotMarkTheRevisionAsSaved()
    {
        using var temporary = new UsageTestDirectory();
        var registry = new DeviceRegistry();
        var store = new UsageStore(temporary.DirectoryPath);
        var tracker = new UsageTracker(registry, store);
        tracker.Load();
        registry.Usage.Add(Phone, 100, 20);
        Assert.True(tracker.SaveIfChanged());
        var valid = File.ReadAllText(temporary.PrimaryPath);
        File.WriteAllText(temporary.PrimaryPath, "broken");
        File.WriteAllText(temporary.BackupPath, "broken");
        registry.Usage.Add(Phone, 5, 3);

        Assert.Throws<IOException>(() => tracker.SaveIfChanged());
        File.WriteAllText(temporary.PrimaryPath, valid);
        File.WriteAllText(temporary.BackupPath, valid);

        Assert.True(tracker.SaveIfChanged());
        Assert.Equal(new DeviceUsage(105, 23), store.Load()[Phone]);
        Assert.False(tracker.SaveIfChanged());
    }

    private static UsageStore CreateStoreWithReader(string directory, Func<string, Stream> openRead)
    {
        var constructor = typeof(UsageStore).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(Func<string, Stream>)],
            modifiers: null);
        Assert.NotNull(constructor);
        return Assert.IsType<UsageStore>(constructor.Invoke([directory, openRead]));
    }

    private static IOException MissingReadError(string path, bool directoryMissing) =>
        directoryMissing
            ? new DirectoryNotFoundException($"Injected missing directory for {path}.")
            : new FileNotFoundException("Injected missing primary.", path);

    private static Dictionary<string, DeviceUsage> Totals(long download, long upload) =>
        new() { [Phone] = new(download, upload) };

    private static string Document(IReadOnlyDictionary<string, DeviceUsage> devices) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = UsageStore.CurrentSchemaVersion,
            savedAtUtc = DateTimeOffset.Parse("2026-11-15T10:00:00Z"),
            devices = devices.ToDictionary(
                pair => pair.Key,
                pair => new
                {
                    downloadBytes = pair.Value.DownloadBytes,
                    uploadBytes = pair.Value.UploadBytes,
                    totalBytes = pair.Value.TotalBytes,
                }),
        });
}

internal sealed class UsageTestDirectory : IDisposable
{
    public UsageTestDirectory()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "opencode", $"UsageMeterTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    public string PrimaryPath => Path.Combine(DirectoryPath, "usage.json");

    public string BackupPath => Path.Combine(DirectoryPath, "usage.backup.json");

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
