using System.Runtime.ExceptionServices;
using System.Security.Principal;
using System.Threading;
using Lantern.App.Services;
using Xunit;

namespace Lantern.App.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void GlobalPerUserMutexCanBeOpenedByTheCurrentUserAndBlocksAnotherThread()
    {
        var instanceName = NewInstanceName();
        using var owner = SingleInstanceGuard.TryAcquireForCurrentUser(instanceName);
        Assert.NotNull(owner);

        var acquired = OnDedicatedThread(() =>
        {
            using var mutex = Mutex.OpenExisting(MutexName(instanceName));
            var ownsMutex = mutex.WaitOne(0);
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }

            return ownsMutex;
        });

        Assert.False(acquired);
    }

    [Fact]
    public void SecondOwnerIsRefusedUntilTheFirstReleasesAndDisposeIsIdempotent()
    {
        var instanceName = NewInstanceName();
        using var owner = SingleInstanceGuard.TryAcquireForCurrentUser(instanceName);
        Assert.NotNull(owner);
        Assert.True(OnDedicatedThread(() =>
        {
            using var duplicate = SingleInstanceGuard.TryAcquireForCurrentUser(instanceName);
            return duplicate is null;
        }));

        owner.Dispose();
        owner.Dispose();

        Assert.True(OnDedicatedThread(() =>
        {
            using var replacement = SingleInstanceGuard.TryAcquireForCurrentUser(instanceName);
            return replacement is not null;
        }));
    }

    [Fact]
    public void AbandonedGlobalMutexCanBeAcquiredAndReleasedByTheNextOwner()
    {
        var instanceName = NewInstanceName();
        Mutex? abandoned = null;
        OnDedicatedThread(() =>
        {
            abandoned = new Mutex(initiallyOwned: true, name: MutexName(instanceName));
            return true;
        });
        try
        {
            using (var replacement = SingleInstanceGuard.TryAcquireForCurrentUser(instanceName))
            {
                Assert.NotNull(replacement);
            }

            Assert.True(OnDedicatedThread(() =>
            {
                using var next = SingleInstanceGuard.TryAcquireForCurrentUser(instanceName);
                return next is not null;
            }));
        }
        finally
        {
            abandoned?.Dispose();
        }
    }

    [Fact]
    public void ExistingNonMutexObjectFailsClosedInsteadOfAllowingAnUnprotectedOwner()
    {
        var instanceName = NewInstanceName();
        using var collision = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            MutexName(instanceName));

        Assert.Throws<WaitHandleCannotBeOpenedException>(() =>
            SingleInstanceGuard.TryAcquireForCurrentUser(instanceName));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Local\\not-allowed")]
    [InlineData("Global\\not-allowed")]
    public void InvalidNamesCannotChangeTheGlobalPerUserNamespace(string instanceName)
    {
        Assert.Throws<ArgumentException>(() => SingleInstanceGuard.TryAcquireForCurrentUser(instanceName));
    }

    private static string NewInstanceName() => $"UsageMeterTests.SingleInstance.{Guid.NewGuid():N}";

    private static string MutexName(string instanceName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return $@"Global\{instanceName}.{identity.User!.Value}";
    }

    private static T OnDedicatedThread<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The isolated mutex check did not finish.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result!;
    }
}
