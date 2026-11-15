using System.Collections.Concurrent;
using System.Text.Json;
using Lantern.Core.Control;
using Lantern.Core.Settings;

namespace Lantern.Core.Usage;

/// <summary>
/// Persists cumulative usage in its own file (usage.json) so a failed write can
/// never damage device names or rules stored in settings.json. Writes follow the
/// same pattern as <see cref="SettingsStore"/>: temporary file, backup copy, then
/// replace.
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

    public UsageStore(string? directory = null)
    {
        this.directory = directory ?? SettingsStore.DefaultDirectory;
        usagePath = Path.Combine(this.directory, "usage.json");
        backupPath = Path.Combine(this.directory, "usage.backup.json");
    }

    public string UsagePath => usagePath;

    public IReadOnlyDictionary<string, DeviceUsage> Load()
    {
        var pathLock = GetPathLock();
        pathLock.Wait();
        try
        {
            UsageDocument? loaded;
            try
            {
                loaded = TryLoad(usagePath, PrimaryLoadAttempts) ??
                         TryLoad(backupPath, attempts: 1);
            }
            catch (IOException)
            {
                // The primary file exists but cannot be read. Fall back to the backup,
                // and if that fails too, surface the error so callers never treat
                // unreadable totals as zero and overwrite them.
                loaded = TryLoad(backupPath, attempts: 1) ?? throw new IOException(
                    $"Saved usage at {usagePath} could not be read.");
            }

            return ToDevices(loaded);
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
        var document = ToDocument(devices);
        var pathLock = GetPathLock();
        pathLock.Wait();
        try
        {
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $"usage-{Guid.NewGuid():N}.tmp");
            var backupTemporaryPath = Path.Combine(
                directory,
                $"usage-backup-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096))
                {
                    JsonSerializer.Serialize(stream, document, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                File.Copy(temporaryPath, backupTemporaryPath);
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
        PathLocks.GetOrAdd(
            Path.GetFullPath(usagePath),
            static _ => new SemaphoreSlim(1, 1));

    private static UsageDocument? TryLoad(string path, int attempts)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096);
                return JsonSerializer.Deserialize<UsageDocument>(stream, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(PrimaryLoadRetryDelay);
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, DeviceUsage> ToDevices(UsageDocument? document)
    {
        var devices = new Dictionary<string, DeviceUsage>(StringComparer.OrdinalIgnoreCase);
        if (document?.Devices is null)
        {
            return devices;
        }

        foreach (var pair in document.Devices)
        {
            string mac;
            try
            {
                mac = TrafficPolicy.NormalizeMac(pair.Key);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                continue;
            }

            if (pair.Value is null)
            {
                continue;
            }

            var current = devices.GetValueOrDefault(mac, DeviceUsage.Empty);
            devices[mac] = new DeviceUsage(
                current.DownloadBytes + Math.Max(0, pair.Value.DownloadBytes),
                current.UploadBytes + Math.Max(0, pair.Value.UploadBytes));
        }

        return devices;
    }

    private static UsageDocument ToDocument(IReadOnlyDictionary<string, DeviceUsage> devices)
    {
        var document = new UsageDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            SavedAtUtc = DateTimeOffset.UtcNow,
        };
        foreach (var pair in devices.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            string mac;
            try
            {
                mac = TrafficPolicy.NormalizeMac(pair.Key);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                continue;
            }

            document.Devices[mac] = new UsageEntry
            {
                DownloadBytes = pair.Value.DownloadBytes,
                UploadBytes = pair.Value.UploadBytes,
                TotalBytes = pair.Value.TotalBytes,
            };
        }

        return document;
    }

    private sealed class UsageDocument
    {
        public int SchemaVersion { get; set; }

        public DateTimeOffset? SavedAtUtc { get; set; }

        public Dictionary<string, UsageEntry?> Devices { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class UsageEntry
    {
        public long DownloadBytes { get; set; }

        public long UploadBytes { get; set; }

        public long TotalBytes { get; set; }
    }
}
