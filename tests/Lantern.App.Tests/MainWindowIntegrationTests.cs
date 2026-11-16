using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Lantern.App;
using Lantern.App.Services;
using Lantern.App.ViewModels;
using Lantern.Core.Control;
using Lantern.Core.Devices;
using Lantern.Core.Networking;
using Lantern.Core.Settings;
using Lantern.Core.Usage;
using Xunit;

namespace Lantern.App.Tests;

[CollectionDefinition("WPF application", DisableParallelization = true)]
public sealed class WpfApplicationCollection : ICollectionFixture<WpfApplicationFixture>
{
}

[Collection("WPF application")]
public sealed class MainWindowIntegrationTests
{
    private readonly WpfApplicationFixture fixture;

    public MainWindowIntegrationTests(WpfApplicationFixture fixture)
    {
        this.fixture = fixture;
    }

    private const string PhoneMac = "021122334455";
    private const string HistoricalMac = "026677889900";
    private const string ComputerMac = "02AABBCCDDEE";
    private static readonly AdapterProfile Adapter = new(
        "test-adapter", "Test adapter", "Synthetic adapter — no capture",
        IPAddress.Parse("192.0.2.10"), 24, IPAddress.Parse("192.0.2.1"),
        PhysicalAddress.Parse(ComputerMac));

    [Fact]
    public void Loading_RestoresPreferenceAndUsageUnionAndKeepsSilentDevicesAndProtectedSelf()
    {
        RunOnDispatcher(async () =>
        {
            var directory = CreateDirectory();
            var settingsStore = new SettingsStore(directory);
            var lastSeen = DateTimeOffset.UtcNow.AddDays(-2);
            await settingsStore.SaveAsync(new AppSettings
            {
                Devices =
                {
                    [PhoneMac] = new DevicePreferences
                    {
                        Alias = "Living room phone",
                        LearnedHostName = "Advertised phone",
                        LastKnownIp = "192.0.2.20",
                        LastSeenUtc = lastSeen,
                    },
                },
            });
            var usageStore = new UsageStore(directory);
            await usageStore.SaveAsync(new Dictionary<string, DeviceUsage>
            {
                [HistoricalMac] = new(1_000, 250),
            });
            var window = CreateWindow(settingsStore, usageStore);
            try
            {
                Assert.False(Control<Button>(window, "StartButton").IsEnabled);
                await window.LoadSavedDataAsync();
                await window.LoadSavedDataAsync();

                Assert.Equal(3, window.Devices.Count);
                var phone = Assert.Single(window.Devices, device => device.MacKey == PhoneMac);
                Assert.Equal("Living room phone", phone.DisplayName);
                Assert.Equal(lastSeen, phone.ObservedAt);
                Assert.False(phone.IsOnline);
                var historical = Assert.Single(window.Devices, device => device.MacKey == HistoricalMac);
                Assert.Equal("-", historical.IpAddress);
                Assert.Equal(1_250, historical.UsageTotalBytes);
                var computer = Assert.Single(window.Devices, device => device.MacKey == ComputerMac);
                Assert.True(computer.IsLocalComputer);
                Assert.True(computer.CanRename);
                Assert.False(computer.CanControl);
                Assert.All(window.Devices, device => Assert.Equal("—", device.DownloadRate));
                Assert.True(Control<Button>(window, "StartButton").IsEnabled);

                Invoke(window, "RefreshDevices", null, EventArgs.Empty);
                Assert.Contains(phone, window.Devices);
                Assert.Contains(historical, window.Devices);
                Assert.Contains(phone, window.DomainRuleDevices);
            }
            finally
            {
                await CloseAsync(window);
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void RenamingProtectedSelf_SavesAliasWithoutChangingOrApplyingTrafficRules()
    {
        RunOnDispatcher(async () =>
        {
            var directory = CreateDirectory();
            var store = new SettingsStore(directory);
            await store.SaveAsync(new AppSettings
            {
                Devices =
                {
                    [ComputerMac] = new DevicePreferences
                    {
                        Alias = "Old desktop name",
                        DownloadKiloBytesPerSecond = 75,
                        UploadKiloBytesPerSecond = 12,
                        PauseInternet = true,
                    },
                },
            });
            var window = CreateWindow(store, new UsageStore(directory));
            try
            {
                await window.LoadSavedDataAsync();
                var computer = Assert.Single(window.Devices, device => device.IsLocalComputer);
                computer.EditableName = "My desktop";
                await InvokeTask(window, "SaveSettingsIfDirtyAsync");

                var saved = (await store.LoadAsync()).Devices[ComputerMac];
                Assert.Equal("My desktop", saved.Alias);
                Assert.Equal(75, saved.DownloadKiloBytesPerSecond);
                Assert.Equal(12, saved.UploadKiloBytesPerSecond);
                Assert.True(saved.PauseInternet);
                var policy = Field<TrafficPolicy>(window, "policy");
                Assert.False(policy.GetRule(ComputerMac).PauseInternet);
                Assert.Equal(0, policy.GetRule(ComputerMac).DownloadKiloBytesPerSecond);

                computer.EditableName = string.Empty;
                await InvokeTask(window, "SaveSettingsIfDirtyAsync");
                Assert.Equal(computer.AutomaticName, computer.DisplayName);
                Assert.Null((await store.LoadAsync()).Devices[ComputerMac].Alias);
            }
            finally
            {
                await CloseAsync(window);
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void ClosingAndReloading_PreservesManualTextAndAddsOnlyPendingObservedUsage()
    {
        RunOnDispatcher(async () =>
        {
            var directory = CreateDirectory();
            var settingsStore = new SettingsStore(directory);
            var usageStore = new UsageStore(directory);
            const string alias = "My phone (192.0.2.5)";
            await settingsStore.SaveAsync(new AppSettings
            {
                Devices =
                {
                    [PhoneMac] = new DevicePreferences
                    {
                        Alias = alias,
                        LearnedHostName = "Automatic phone name",
                        LastKnownIp = "192.0.2.20",
                    },
                },
            });
            await usageStore.SaveAsync(new Dictionary<string, DeviceUsage>
            {
                [PhoneMac] = new(1_000, 500),
            });
            var first = CreateWindow(settingsStore, usageStore);
            try
            {
                await first.LoadSavedDataAsync();
                var registry = Field<DeviceRegistry>(first, "registry");
                registry.RecordTraffic(PhysicalAddress.Parse(PhoneMac), TrafficDirection.Download, 123);
                registry.RecordTraffic(PhysicalAddress.Parse(PhoneMac), TrafficDirection.Upload, 77);
            }
            finally
            {
                await CloseAsync(first);
            }

            var restored = CreateWindow(new SettingsStore(directory), new UsageStore(directory));
            try
            {
                await restored.LoadSavedDataAsync();
                var phone = Assert.Single(restored.Devices, device => device.MacKey == PhoneMac);
                Assert.Equal(alias, phone.DisplayName);
                Assert.Equal("192.0.2.20", phone.IpAddress);
                Assert.Equal("Automatic phone name", phone.AutomaticName);
                Assert.Equal(1_123, phone.UsageDownloadBytes);
                Assert.Equal(577, phone.UsageUploadBytes);
                Assert.Equal(1_700, phone.UsageTotalBytes);
                Assert.Equal("—", phone.DownloadRate);
                Assert.False(phone.IsOnline);
            }
            finally
            {
                await CloseAsync(restored);
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void Closing_CommitsAliasAndDebouncedLimitBindingsBeforeSaving()
    {
        RunOnDispatcher(async () =>
        {
            var directory = CreateDirectory();
            var store = new SettingsStore(directory);
            await store.SaveAsync(new AppSettings
            {
                Devices =
                {
                    [PhoneMac] = new DevicePreferences { Alias = "Before editing" },
                },
            });
            var window = CreateWindow(store, new UsageStore(directory));
            try
            {
                await window.LoadSavedDataAsync();
                Control<DataGrid>(window, "DeviceGrid").SelectedItem =
                    Assert.Single(window.Devices, device => device.MacKey == PhoneMac);
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.DataBind);
                Control<TextBox>(window, "DeviceNameEditor").SetCurrentValue(TextBox.TextProperty, "Last typed name");
                Control<TextBox>(window, "DownloadLimitEditor").SetCurrentValue(TextBox.TextProperty, "345");
                Control<TextBox>(window, "UploadLimitEditor").SetCurrentValue(TextBox.TextProperty, "67");
                await CloseAsync(window);

                var saved = (await store.LoadAsync()).Devices[PhoneMac];
                Assert.Equal("Last typed name", saved.Alias);
                Assert.Equal(345, saved.DownloadKiloBytesPerSecond);
                Assert.Equal(67, saved.UploadKiloBytesPerSecond);
            }
            finally
            {
                if (!Field<bool>(window, "closeAfterStop"))
                {
                    await CloseAsync(window);
                }

                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void RememberedGatewayIp_DoesNotProtectAClientOrSuppressItsSavedPolicy()
    {
        RunOnDispatcher(async () =>
        {
            var directory = CreateDirectory();
            var store = new SettingsStore(directory);
            await store.SaveAsync(new AppSettings
            {
                Devices =
                {
                    [PhoneMac] = new DevicePreferences
                    {
                        LastKnownIp = Adapter.GatewayAddress.ToString(),
                        PauseInternet = true,
                        DownloadKiloBytesPerSecond = 120,
                    },
                },
            });
            var window = CreateWindow(store, new UsageStore(directory));
            try
            {
                await window.LoadSavedDataAsync();
                var phone = Assert.Single(window.Devices, device => device.MacKey == PhoneMac);
                Assert.False(phone.IsProtected);
                Assert.True(phone.CanControl);
                Invoke(window, "ApplyAllSavedRules");
                var policy = Field<TrafficPolicy>(window, "policy");
                Assert.True(policy.GetRule(PhoneMac).PauseInternet);
                Field<DeviceRegistry>(window, "registry").Observe(
                    IPAddress.Parse("192.0.2.20"), PhysicalAddress.Parse(PhoneMac), DateTimeOffset.UtcNow);
                Invoke(window, "RefreshDevices", null, EventArgs.Empty);
                Assert.True(policy.GetRule(PhoneMac).PauseInternet);
                Assert.Equal(120, policy.GetRule(PhoneMac).DownloadKiloBytesPerSecond);
                Assert.Equal("192.0.2.20", phone.IpAddress);
            }
            finally
            {
                await CloseAsync(window);
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void DomainBatches_PreserveMetadataRetainSilentGroupsAndDoNotRewriteDropHistory()
    {
        RunOnDispatcher(async () =>
        {
            var directory = CreateDirectory();
            var store = new SettingsStore(directory);
            await store.SaveAsync(new AppSettings
            {
                Devices =
                {
                    [PhoneMac] = new DevicePreferences
                    {
                        Alias = "Phone",
                        LastKnownIp = "192.0.2.20",
                        LastSeenUtc = DateTimeOffset.UtcNow.AddDays(-1),
                    },
                },
            });
            var window = CreateWindow(store, new UsageStore(directory));
            try
            {
                await window.LoadSavedDataAsync();
                var at = DateTimeOffset.UtcNow.AddMinutes(-5);
                Observe(window, "example.com", DomainObservationSource.Tls, at.AddSeconds(2), dropped: true);
                Observe(window, "example.com", DomainObservationSource.Dns, at);
                Observe(window, "example.com", DomainObservationSource.Http, at.AddSeconds(1));
                Assert.Empty(window.WebsiteActivity);

                Invoke(window, "DrainDomainObservations", null, EventArgs.Empty);
                var row = Assert.Single(window.WebsiteActivity);
                Assert.Equal(3, row.ObservationCount);
                Assert.Equal(at, row.FirstSeen);
                Assert.Equal(at.AddSeconds(2), row.LastSeen);
                Assert.Equal("DNS + TLS + HTTP", row.SourceLabel);
                Assert.True(row.LastObservedDrop);
                Invoke(window, "RefreshDevices", null, EventArgs.Empty);
                Assert.Single(window.DeviceActivityGroups);
                Assert.False(Assert.Single(window.Devices, device => device.MacKey == PhoneMac).IsOnline);

                var policy = Field<TrafficPolicy>(window, "policy");
                policy.SetBlockedDomains(PhoneMac, ["example.com"]);
                Invoke(window, "RefreshBlockedActivityState");
                Assert.True(row.IsBlocked);
                policy.SetBlockedDomains(PhoneMac, []);
                Invoke(window, "RefreshBlockedActivityState");
                Assert.False(row.IsBlocked);
                Assert.True(row.LastObservedDrop);

                for (var index = 0; index < 300; index++)
                {
                    Observe(window, $"d{index}.example", DomainObservationSource.Dns, at.AddSeconds(index));
                }

                Invoke(window, "DrainDomainObservations", null, EventArgs.Empty);
                Assert.Equal(250, window.WebsiteActivity.Count);
                Assert.Equal(250, Assert.Single(window.DeviceActivityGroups).Domains.Count);
                Assert.Equal(0, Field<UsageTracker>(window, "usageTracker").Ledger.Get(PhoneMac).TotalBytes);

                Invoke(window, "ClearObservedActivity");
                Observe(window, "queued-old.example", DomainObservationSource.Dns, at);
                Invoke(window, "DrainDomainObservations", null, EventArgs.Empty);
                Assert.Empty(window.WebsiteActivity);
                Assert.Empty(window.DeviceActivityGroups);
            }
            finally
            {
                await CloseAsync(window);
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public void DelayedIdentity_IsRevalidatedAfterWaitingForTheSaveGate()
    {
        RunOnDispatcher(async () =>
        {
            var directory = CreateDirectory();
            var store = new SettingsStore(directory);
            await store.SaveAsync(new AppSettings
            {
                Devices =
                {
                    [PhoneMac] = new DevicePreferences
                    {
                        Alias = "Manual name",
                        LearnedHostName = "Saved automatic name",
                        LastKnownIp = "192.0.2.20",
                        PauseInternet = true,
                    },
                },
            });
            var window = CreateWindow(store, new UsageStore(directory));
            try
            {
                await window.LoadSavedDataAsync();
                var gate = Field<SemaphoreSlim>(window, "identitySaveSync");
                await gate.WaitAsync();
                var pending = InvokeTask(window, "PersistLearnedIdentityAsync", new DeviceIdentityLearnedEventArgs(
                    PhysicalAddress.Parse(PhoneMac), "Late wrong binding", 0, IPAddress.Parse("192.0.2.20")));
                Assert.False(pending.IsCompleted);
                gate.Release();
                await pending;

                var preferences = Field<AppSettings>(window, "settings").Devices[PhoneMac];
                Assert.Equal("Saved automatic name", preferences.LearnedHostName);
                Assert.Equal("Manual name", preferences.Alias);
                Assert.True(preferences.PauseInternet);
                Assert.False(Field<TrafficPolicy>(window, "policy").GetRule(PhoneMac).PauseInternet);
            }
            finally
            {
                await CloseAsync(window);
                Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Theory]
    [InlineData("usage")]
    [InlineData("settings")]
    public void CorruptSavedCopies_DisableStartAndAreNeverReplaced(string name)
    {
        RunOnDispatcher(async () =>
        {
            var directory = CreateDirectory();
            const string corrupt = "{ invalid saved data";
            var primary = Path.Combine(directory, $"{name}.json");
            var backup = Path.Combine(directory, $"{name}.backup.json");
            await File.WriteAllTextAsync(primary, corrupt);
            await File.WriteAllTextAsync(backup, corrupt);
            var window = CreateWindow(new SettingsStore(directory), new UsageStore(directory));
            try
            {
                await window.LoadSavedDataAsync();
                Assert.False(Control<Button>(window, "StartButton").IsEnabled);
                var warning = Control<TextBlock>(window, "SavedDataWarningText");
                Assert.Equal(Visibility.Visible, warning.Visibility);
                Assert.Contains("warning", warning.Text, StringComparison.OrdinalIgnoreCase);
                Assert.False(Control<Expander>(window, "CaptureDiagnosticsExpander").IsExpanded);
                window.SaveUsageNow();
            }
            finally
            {
                await CloseAsync(window);
            }

            Assert.Equal(corrupt, await File.ReadAllTextAsync(primary));
            Assert.Equal(corrupt, await File.ReadAllTextAsync(backup));
            Directory.Delete(directory, recursive: true);
        });
    }

    private static MainWindow CreateWindow(SettingsStore settingsStore, UsageStore usageStore) =>
        new(settingsStore, usageStore, () => [Adapter]);

    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));

    private static T Field<T>(MainWindow window, string name) =>
        Assert.IsType<T>(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));

    private static object? Invoke(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);

    private static Task InvokeTask(MainWindow window, string name, params object?[] arguments) =>
        Assert.IsAssignableFrom<Task>(Invoke(window, name, arguments));

    private static void Observe(
        MainWindow window,
        string domain,
        DomainObservationSource source,
        DateTimeOffset at,
        bool dropped = false) =>
        Invoke(window, "Engine_OnDeviceDomainObserved", null,
            new DeviceDomainObservedEventArgs(PhysicalAddress.Parse(PhoneMac),
                new DomainObservation(domain, source, IPAddress.Parse("203.0.113.25")), at, dropped));

    private static async Task CloseAsync(MainWindow window)
    {
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult(true);
        window.Close();
        await closed.Task;
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opencode", $"UsageMeterUiTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void RunOnDispatcher(Func<Task> action) => fixture.Run(action);
}
