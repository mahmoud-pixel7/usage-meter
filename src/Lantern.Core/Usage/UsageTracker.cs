using Lantern.Core.Devices;

namespace Lantern.Core.Usage;

/// <summary>
/// Connects a <see cref="DeviceRegistry"/>'s usage ledger to a <see cref="UsageStore"/>:
/// restores saved totals on startup and writes them back only when they changed.
/// </summary>
public sealed class UsageTracker
{
    private readonly DeviceRegistry registry;
    private readonly UsageStore store;
    private readonly object saveSync = new();
    private long savedRevision;
    private volatile bool loaded;

    public UsageTracker(DeviceRegistry registry, UsageStore store)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public bool IsLoaded => loaded;

    public UsageLedger Ledger => registry.Usage;

    public void Load()
    {
        lock (saveSync)
        {
            if (loaded)
            {
                return;
            }

            registry.Usage.Restore(store.Load());
            loaded = true;
        }
    }

    public Task LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Load, cancellationToken);

    /// <summary>
    /// Writes the ledger when it changed since the last save. Nothing is written
    /// before saved totals have been loaded, so a partial ledger can never
    /// overwrite the file.
    /// </summary>
    public bool SaveIfChanged()
    {
        lock (saveSync)
        {
            if (!loaded)
            {
                return false;
            }

            var snapshot = registry.Usage.Export();
            if (snapshot.Revision == savedRevision)
            {
                return false;
            }

            store.Save(snapshot.Devices);
            savedRevision = snapshot.Revision;
            return true;
        }
    }

    public Task<bool> SaveIfChangedAsync(CancellationToken cancellationToken = default) =>
        Task.Run(SaveIfChanged, cancellationToken);

    /// <summary>
    /// Moves bytes that have not been through a snapshot yet into the ledger and saves.
    /// Used when the app closes or Windows ends the session.
    /// </summary>
    public bool FlushAndSave()
    {
        registry.FlushPendingUsage();
        return SaveIfChanged();
    }
}
