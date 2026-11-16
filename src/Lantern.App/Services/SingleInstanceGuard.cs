using System.Security.Principal;
using System.Threading;

namespace Lantern.App.Services;

/// <summary>
/// Holds process ownership for one Windows user across sessions. Dispose on the
/// thread that acquired the guard, because Windows mutex ownership is thread-affine.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private Mutex? mutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        this.mutex = mutex;
    }

    /// <summary>
    /// Returns null when another owner holds the mutex. A distinct instance name
    /// isolates tests; the default name is shared by all Usage Meter installations.
    /// Access-denied and incompatible named-object errors are allowed to fail closed.
    /// </summary>
    public static SingleInstanceGuard? TryAcquireForCurrentUser(string instanceName = "UsageMeter.SingleInstance")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        if (instanceName.Contains('\\'))
        {
            throw new ArgumentException("The instance name cannot contain a namespace separator.", nameof(instanceName));
        }

        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User?.Value ??
                      throw new InvalidOperationException("The current Windows user could not be identified.");
        var mutex = new Mutex(initiallyOwned: false, name: $@"Global\{instanceName}.{userSid}");
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return null;
            }

            return new SingleInstanceGuard(mutex);
        }
        catch
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }

            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        var owned = mutex;
        if (owned is null)
        {
            return;
        }

        mutex = null;
        try
        {
            owned.ReleaseMutex();
        }
        finally
        {
            owned.Dispose();
        }
    }
}
