using System.Collections.Concurrent;
using System.Text.Json;
using Lantern.Core.Control;
using Lantern.Core.Settings;

namespace Lantern.Core.Usage;

/// <summary>
/// Persists cumulative usage separately from settings. Both copies are validated
/// before a save; unreadable history must never be mistaken for a fresh ledger.
/// </summary>
public sealed class UsageStore
{
    public const int CurrentSchemaVersion = 1;
    private const int PrimaryLoadAttempts = 6;
    private static readonly TimeSpan PrimaryLoadRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string directory;
    private readonly string usagePath;
    private readonly string backupPath;
    private readonly Func<string, Stream> openRead;

    public UsageStore(string? directory = null)
        : this(directory, OpenRead)
    {
    }

    private UsageStore(string? directory, Func<string, Stream> openRead)
    {
        this.directory = Path.GetFullPath(directory ?? SettingsStore.DefaultDirectory);
        usagePath = Path.Combine(this.directory, "usage.json");
        backupPath = Path.Combine(this.directory, "usage.backup.json");
        this.openRead = openRead;
    }

    public string UsagePath => usagePath;

    public IReadOnlyDictionary<string, DeviceUsage> Load()
    {
        var pathLock = GetPathLock();
        pathLock.Wait();
        try
        {
            return ReadSavedDevices();
        }
        finally
        {
            pathLock.Release();
        }
    }

    public Task<IReadOnlyDictionary<string, DeviceUsage>> LoadAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(Load, cancellationToken);

    public void Save(IReadOnlyDictionary<string, DeviceUsage> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var normalized = NormalizeForSave(devices);
        var document = ToDocument(normalized);
        var pathLock = GetPathLock();
        pathLock.Wait();
        try
        {
            var saved = ReadSavedDevices();
            if (!ContainsAllTotals(normalized, saved))
            {
                throw new IOException(
                    "Saved usage cannot be replaced by lower or incomplete totals. " +
                    "The existing usage files have not been changed.");
            }

            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $"usage-{Guid.NewGuid():N}.tmp");
            var backupTemporaryPath = Path.Combine(
                directory,
                $"usage-backup-{Guid.NewGuid():N}.tmp");
            try
            {
                WriteDocument(temporaryPath, document);
                WriteDocument(backupTemporaryPath, document);
                File.Move(backupTemporaryPath, backupPath, overwrite: true);
                File.Move(temporaryPath, usagePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                if (File.Exists(backupTemporaryPath))
                {
                    File.Delete(backupTemporaryPath);
                }
            }
        }
        finally
        {
            pathLock.Release();
        }
    }

    public Task SaveAsync(
        IReadOnlyDictionary<string, DeviceUsage> devices,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Save(devices), cancellationToken);

    private SemaphoreSlim GetPathLock() =>
        PathLocks.GetOrAdd(usagePath, static _ => new SemaphoreSlim(1, 1));

    private IReadOnlyDictionary<string, DeviceUsage> ReadSavedDevices()
    {
        var primary = ReadCopy(usagePath, PrimaryLoadAttempts);
        var backup = ReadCopy(backupPath, attempts: 1);
        var unsupported = primary.Error as UnsupportedUsageSchemaException ??
                          backup.Error as UnsupportedUsageSchemaException;
        if (unsupported is not null)
        {
            throw new IOException(
                $"Saved usage in {directory} uses a newer schema. " +
                "The usage files have not been changed.",
                unsupported);
        }

        if (primary.Devices is not null && backup.Devices is not null)
        {
            if (ContainsAllTotals(primary.Devices, backup.Devices))
            {
                return primary.Devices;
            }

            // A crash can leave the newly written backup ahead of the primary.
            if (ContainsAllTotals(backup.Devices, primary.Devices))
            {
                return backup.Devices;
            }

            throw new IOException(
                "The usage files contain conflicting totals. Neither copy can be " +
                "used without losing history, so the files have not been changed.");
        }

        if (primary.Devices is not null || backup.Devices is not null)
        {
            return primary.Devices ?? backup.Devices!;
        }

        if (primary.Error is null && backup.Error is null)
        {
            return new Dictionary<string, DeviceUsage>(StringComparer.OrdinalIgnoreCase);
        }

        throw new IOException(
            $"Saved usage at {usagePath} and {backupPath} could not be read. " +
            "The existing files have not been changed.",
            new AggregateException(new[] { primary.Error, backup.Error }.OfType<Exception>()));
    }

    private static Stream OpenRead(string path) => new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read | FileShare.Delete,
        bufferSize: 4096);

    private LoadedCopy ReadCopy(string path, int attempts)
    {
        Exception? readError = null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = openRead(path);
                return new LoadedCopy(ReadDevices(stream), null);
            }
            catch (FileNotFoundException)
            {
                return new LoadedCopy(null, readError);
            }
            catch (DirectoryNotFoundException)
            {
                return new LoadedCopy(null, readError);
            }
            catch (UnsupportedUsageSchemaException exception)
            {
                return new LoadedCopy(null, exception);
            }
            catch (JsonException exception)
            {
                return new LoadedCopy(null, exception);
            }
            catch (IOException exception) when (attempt < attempts)
            {
                readError = exception;
                Thread.Sleep(PrimaryLoadRetryDelay);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new LoadedCopy(null, exception);
            }
        }
    }

    private static IReadOnlyDictionary<string, DeviceUsage> ReadDevices(Stream stream)
    {
        using var json = JsonDocument.Parse(stream);
        var document = ReadObject(json.RootElement, "usage document");
        if (!document.TryGetValue("schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version))
        {
            throw new JsonException("The usage schema version is missing or invalid.");
        }

        if (version > CurrentSchemaVersion)
        {
            throw new UnsupportedUsageSchemaException(version);
        }

        if (version != CurrentSchemaVersion ||
            !document.TryGetValue("devices", out var entries) ||
            entries.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The usage schema or devices collection is invalid.");
        }

        if (document.TryGetValue("savedAtUtc", out var savedAt) &&
            savedAt.ValueKind != JsonValueKind.Null &&
            (savedAt.ValueKind != JsonValueKind.String || !savedAt.TryGetDateTimeOffset(out _)))
        {
            throw new JsonException("The usage save timestamp is invalid.");
        }

        var devices = new Dictionary<string, DeviceUsage>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.EnumerateObject())
        {
            string mac;
            try
            {
                mac = TrafficPolicy.NormalizeMac(entry.Name);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                throw new JsonException("A saved usage MAC address is invalid.", exception);
            }

            var values = ReadObject(entry.Value, $"usage entry for {mac}");
            var usage = new DeviceUsage(
                ReadCounter(values, "downloadBytes"),
                ReadCounter(values, "uploadBytes"));
            var total = ReadCounter(values, "totalBytes");
            if (!HasValidCounters(usage) || total != usage.TotalBytes)
            {
                throw new JsonException($"The saved usage totals for {mac} are inconsistent.");
            }

            if (!devices.TryAdd(mac, usage))
            {
                throw new JsonException($"The saved usage contains duplicate entries for {mac}.");
            }
        }

        return devices;
    }

    private static Dictionary<string, JsonElement> ReadObject(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"The {description} must be an object.");
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                throw new JsonException($"The {description} contains a duplicate {property.Name} field.");
            }
        }

        return properties;
    }

    private static long ReadCounter(IReadOnlyDictionary<string, JsonElement> entry, string name)
    {
        if (!entry.TryGetValue(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var counter) || counter < 0)
        {
            throw new JsonException($"The usage {name} counter is missing or invalid.");
        }

        return counter;
    }

    private static bool HasValidCounters(DeviceUsage? usage) =>
        usage is { DownloadBytes: >= 0, UploadBytes: >= 0 } &&
        usage.DownloadBytes <= long.MaxValue - usage.UploadBytes;

    private static Dictionary<string, DeviceUsage> NormalizeForSave(
        IReadOnlyDictionary<string, DeviceUsage> devices)
    {
        var normalized = new Dictionary<string, DeviceUsage>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in devices)
        {
            string mac;
            try
            {
                mac = TrafficPolicy.NormalizeMac(pair.Key);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                throw new ArgumentException("A usage MAC address is invalid.", nameof(devices), exception);
            }

            if (!HasValidCounters(pair.Value))
            {
                throw new ArgumentException("Usage counters must be nonnegative and cannot overflow.", nameof(devices));
            }

            if (!normalized.TryAdd(mac, pair.Value))
            {
                throw new ArgumentException($"Usage contains duplicate entries for {mac}.", nameof(devices));
            }
        }

        return normalized;
    }

    private static bool ContainsAllTotals(
        IReadOnlyDictionary<string, DeviceUsage> candidate,
        IReadOnlyDictionary<string, DeviceUsage> saved) =>
        saved.All(pair => candidate.TryGetValue(pair.Key, out var current) &&
                          current.DownloadBytes >= pair.Value.DownloadBytes &&
                          current.UploadBytes >= pair.Value.UploadBytes);

    private static UsageDocument ToDocument(IReadOnlyDictionary<string, DeviceUsage> devices)
    {
        var document = new UsageDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            SavedAtUtc = DateTimeOffset.UtcNow,
        };
        foreach (var pair in devices.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            document.Devices.Add(pair.Key, new UsageEntry
            {
                DownloadBytes = pair.Value.DownloadBytes,
                UploadBytes = pair.Value.UploadBytes,
                TotalBytes = pair.Value.TotalBytes,
            });
        }

        return document;
    }

    private static void WriteDocument(string path, UsageDocument document)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096);
        JsonSerializer.Serialize(stream, document, JsonOptions);
        stream.Flush(flushToDisk: true);
    }

    private sealed record LoadedCopy(
        IReadOnlyDictionary<string, DeviceUsage>? Devices,
        Exception? Error);

    private sealed class UnsupportedUsageSchemaException(int version) : IOException(
        $"Usage schema version {version} is newer than supported version {CurrentSchemaVersion}.")
    {
    }

    private sealed class UsageDocument
    {
        public int SchemaVersion { get; set; }

        public DateTimeOffset? SavedAtUtc { get; set; }

        public Dictionary<string, UsageEntry> Devices { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class UsageEntry
    {
        public long DownloadBytes { get; set; }

        public long UploadBytes { get; set; }

        public long TotalBytes { get; set; }
    }
}
