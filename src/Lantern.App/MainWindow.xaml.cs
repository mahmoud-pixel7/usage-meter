using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lantern.App.Controls;
using Lantern.App.Services;
using Lantern.App.ViewModels;
using Lantern.Core.Control;
using Lantern.Core.Devices;
using Lantern.Core.Networking;
using Lantern.Core.Settings;
using Lantern.Core.Usage;

namespace Lantern.App;

public partial class MainWindow : Window
{
    private const int MaxWebsiteActivityRows = 250;
    private static readonly TimeSpan UsageSaveInterval = TimeSpan.FromSeconds(30);
    private readonly DeviceRegistry registry = new();
    private readonly TrafficPolicy policy = new();
    private readonly SettingsStore settingsStore;
    private readonly UsageTracker usageTracker;
    private readonly Func<IReadOnlyList<AdapterProfile>> getAdapters;
    private readonly SemaphoreSlim identitySaveSync = new(1, 1);
    private readonly SemaphoreSlim settingsSaveSync = new(1, 1);
    private readonly DispatcherTimer refreshTimer;
    private readonly DispatcherTimer domainRefreshTimer;
    private readonly DispatcherTimer settingsSaveTimer;
    private readonly DomainObservationBuffer domainBuffer = new();
    private readonly Dictionary<string, double> pageScrollOffsets = new(StringComparer.Ordinal);
    private UsagePersistenceService? usagePersistence;
    private TaskCompletionSource<bool>? operationCompletion;
    private string currentPageName = "OverviewSection";
    private string? loadWarning;
    private string? usageSaveWarning;
    private string? settingsSaveWarning;
    private long omittedDomainObservations;
    private long settingsRevision;
    private long savedSettingsRevision;
    private bool dataReady;
    private bool loadStarted;
    private bool closingInProgress;
    private bool deviceOrderingDirty;
    private readonly Dictionary<string, DeviceViewModel> deviceIndex =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceActivityViewModel> websiteActivityIndex =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceActivityGroupViewModel> websiteActivityGroupIndex =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TrafficHistory trafficHistory = new(TrafficSamplingProfile.Capacity, TrafficSamplingProfile.Retention);
    private PcapLanEngine engine;
    private AppSettings settings = new();
    private DateTimeOffset? controlStartedAt;
    private bool operationInProgress;
    private bool closeAfterStop;

    public string VersionText =>
        $"v{(typeof(MainWindow).Assembly.GetName().Version ?? new Version()).ToString(3)}";

    public MainWindow()
        : this(new SettingsStore(), new UsageStore(), WindowsAdapterService.GetUsableAdapters)
    {
    }

    internal MainWindow(
        SettingsStore settingsStore,
        UsageStore usageStore,
        Func<IReadOnlyList<AdapterProfile>> getAdapters)
    {
        this.settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        this.getAdapters = getAdapters ?? throw new ArgumentNullException(nameof(getAdapters));
        InitializeComponent();
        DataContext = this;
        SidebarVersionText.Text = VersionText;
        DomainPresetSelector.ItemsSource = DomainPresets;
        usageTracker = new UsageTracker(registry, usageStore);
        engine = new PcapLanEngine(registry, policy);
        engine.StatusChanged += Engine_OnStatusChanged;
        engine.StateChanged += Engine_OnStateChanged;
        engine.DeviceIdentityLearned += Engine_OnDeviceIdentityLearned;
        engine.DeviceDomainObserved += Engine_OnDeviceDomainObserved;
        refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TrafficSamplingProfile.Interval,
        };
        refreshTimer.Tick += RefreshDevices;
        domainRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        domainRefreshTimer.Tick += DrainDomainObservations;
        settingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = UsageSaveInterval,
        };
        settingsSaveTimer.Tick += SettingsSaveTimer_OnTick;
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;

        var view = CollectionViewSource.GetDefaultView(Devices);
        view.SortDescriptions.Add(
            new SortDescription(nameof(DeviceViewModel.IsProtected), ListSortDirection.Ascending));
        view.SortDescriptions.Add(
            new SortDescription(nameof(DeviceViewModel.DisplayName), ListSortDirection.Ascending));

        var groupView = CollectionViewSource.GetDefaultView(DeviceActivityGroups);
        groupView.SortDescriptions.Add(
            new SortDescription(nameof(DeviceActivityGroupViewModel.DeviceName), ListSortDirection.Ascending));

        var domainRuleView = CollectionViewSource.GetDefaultView(DomainRules);
        domainRuleView.SortDescriptions.Add(
            new SortDescription(nameof(DomainRuleViewModel.DeviceName), ListSortDirection.Ascending));
        domainRuleView.SortDescriptions.Add(
            new SortDescription(nameof(DomainRuleViewModel.Domain), ListSortDirection.Ascending));

        var domainPresetRuleView = CollectionViewSource.GetDefaultView(DomainPresetRules);
        domainPresetRuleView.SortDescriptions.Add(
            new SortDescription(nameof(DomainPresetRuleViewModel.DeviceName), ListSortDirection.Ascending));
        domainPresetRuleView.SortDescriptions.Add(
            new SortDescription(nameof(DomainPresetRuleViewModel.PresetName), ListSortDirection.Ascending));
        CollectionViewSource.GetDefaultView(DomainRuleDevices).SortDescriptions.Add(
            new SortDescription(nameof(DeviceViewModel.DisplayName), ListSortDirection.Ascending));
        UpdateButtons();
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    public ObservableCollection<DeviceActivityViewModel> WebsiteActivity { get; } = [];

    public ObservableCollection<DeviceActivityGroupViewModel> DeviceActivityGroups { get; } = [];

    public ObservableCollection<DeviceViewModel> DomainRuleDevices { get; } = [];

    public ObservableCollection<DomainRuleViewModel> DomainRules { get; } = [];

    public ObservableCollection<DomainPresetRuleViewModel> DomainPresetRules { get; } = [];

    public IReadOnlyList<DomainBlockPreset> DomainPresets => DomainBlockPresetCatalog.All;

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs eventArgs) =>
        await LoadSavedDataAsync();

    internal async Task LoadSavedDataAsync()
    {
        if (loadStarted)
        {
            return;
        }

        loadStarted = true;
        // Usage Meter must not offer an upstream LANtern release as its own update.
        var loadErrors = new List<string>();
        try
        {
            await usageTracker.LoadAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            loadErrors.Add($"Could not load saved usage: {exception.Message}");
        }

        try
        {
            settings = await settingsStore.LoadAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            loadErrors.Add($"Could not load device settings: {exception.Message}");
        }

        if (closingInProgress)
        {
            return;
        }

        loadWarning = loadErrors.Count == 0 ? null : string.Join("  ", loadErrors);
        dataReady = loadWarning is null;
        registry.RestoreInventory(SavedDeviceInventory.Build(settings, usageTracker.Ledger));
        LoadDomainRulesFromSettings();
        SelectedDeviceDetails.IsEnabled = dataReady;
        DomainRulesSection.IsEnabled = dataReady;
        if (dataReady)
        {
            StartUsagePersistence();
            domainRefreshTimer.Start();
            settingsSaveTimer.Start();
        }

        var adapters = getAdapters();
        AdapterSelector.ItemsSource = adapters;
        AdapterSelector.SelectedIndex = adapters.Count > 0 ? 0 : -1;
        RefreshDevices(null, EventArgs.Empty);
        UpdateButtons();
        if (!dataReady)
        {
            SetStatus("Monitoring is disabled to protect saved data. Close the app and recover its saved files before retrying.", false);
        }
        else if (adapters.Count == 0)
        {
            SetStatus("No active IPv4 adapter with a gateway was found.", false);
        }
        else
        {
            SetStatus("Saved devices loaded. Monitoring is stopped.", null);
        }

        RefreshCaptureDiagnostics();
    }

    private void StartUsagePersistence()
    {
        if (usagePersistence is null)
        {
            usagePersistence = new UsagePersistenceService(usageTracker, UsageSaveInterval);
            usagePersistence.SaveFailed += UsagePersistence_OnSaveFailed;
            usagePersistence.SaveSucceeded += UsagePersistence_OnSaveSucceeded;
        }

        usagePersistence.Start();
    }

    private void UsagePersistence_OnSaveFailed(object? sender, string message)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            usageSaveWarning = message;
            DetailStatusText.Text = message;
            RefreshCaptureDiagnostics();
        });
    }

    private void UsagePersistence_OnSaveSucceeded(object? sender, EventArgs eventArgs)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (usageSaveWarning is null)
            {
                return;
            }

            usageSaveWarning = null;
            RefreshCaptureDiagnostics();
        });
    }

    private async void SettingsSaveTimer_OnTick(object? sender, EventArgs eventArgs) =>
        await TrySaveSettingsAsync();

    /// <summary>
    /// Synchronously records bytes that have not reached a snapshot yet and writes
    /// usage.json. Called when the window closes and when Windows ends the session.
    /// </summary>
    public void SaveUsageNow()
    {
        if (!dataReady)
        {
            return;
        }

        try
        {
            usageTracker.FlushAndSave();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Shutdown may not allow UI; keep the previous durable copies intact.
        }
    }

    private async void StartButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (!dataReady || closingInProgress || operationInProgress ||
            AdapterSelector.SelectedItem is not AdapterProfile selected)
        {
            return;
        }

        BeginOperation();
        SetStatus("Starting packet capture…", null);
        try
        {
            ClearObservedActivity();
            ApplyAllSavedRules();
            UpdateKnownDeviceHints();
            await engine.StartAsync(selected, CancellationToken.None);
            controlStartedAt = DateTimeOffset.UtcNow;
            TrafficChart.Samples = trafficHistory.Samples;
            RefreshDevices(null, EventArgs.Empty);
            SetStatus("IPv4 control active — displaying observed traffic only.", true);
            refreshTimer.Start();
            await TrySaveSettingsAsync();
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, false);
            MessageBox.Show(
                exception.Message,
                "Could not start LAN control",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            CompleteOperation();
        }
    }

    private async void StopButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        await StopEngineAsync();
    }

    private async void ScanButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (closingInProgress || operationInProgress || !engine.IsRunning)
        {
            return;
        }

        BeginOperation();
        try
        {
            SetStatus("Sending a full /24 ARP sweep…", null);
            UpdateKnownDeviceHints();
            var result = await engine.RefreshNeighborsAsync();
            RefreshDevices(null, EventArgs.Empty);
            SetStatus(result.StatusMessage, true);
            await TrySaveSettingsAsync();
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, false);
        }
        finally
        {
            CompleteOperation();
        }
    }

    private void ClearActivityButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        ClearObservedActivity();
        DetailStatusText.Text = "Observed domains cleared from this session; usage totals and saved rules are unchanged.";
    }

    private void ClearObservedActivity()
    {
        domainBuffer.Clear(DateTimeOffset.UtcNow);
        omittedDomainObservations = 0;
        websiteActivityIndex.Clear();
        WebsiteActivity.Clear();
        websiteActivityGroupIndex.Clear();
        DeviceActivityGroups.Clear();
        RefreshWebsiteActivityState();
        RefreshCaptureDiagnostics();
    }

    private void Engine_OnDeviceDomainObserved(
        object? sender,
        DeviceDomainObservedEventArgs eventArgs)
    {
        domainBuffer.Add(eventArgs);
    }

    private void DrainDomainObservations(object? sender, EventArgs eventArgs)
    {
        var batch = domainBuffer.Drain();
        var omissionChanged = omittedDomainObservations != batch.OmittedObservations;
        omittedDomainObservations = batch.OmittedObservations;
        foreach (var observation in batch.Observations)
        {
            ObserveWebsiteActivity(observation);
        }

        if (batch.Observations.Count > 0)
        {
            TrimWebsiteActivity();
            SyncWebsiteActivityGroups();
        }

        if (batch.Observations.Count > 0 || omissionChanged)
        {
            RefreshWebsiteActivityState();
        }

        if (omissionChanged)
        {
            RefreshCaptureDiagnostics();
        }
    }

    private void ObserveWebsiteActivity(BufferedDomainObservation observation)
    {
        var macKey = TrafficPolicy.NormalizeMac(observation.MacAddress.ToString());
        var deviceName = ResolveDeviceName(macKey);
        deviceIndex.TryGetValue(macKey, out var device);
        var ipAddress = device?.IpAddress ?? "-";
        var group = GetOrCreateWebsiteActivityGroup(macKey, deviceName, ipAddress);
        var domainKey = $"{macKey}|{observation.Domain}";
        var isNew = !websiteActivityIndex.TryGetValue(domainKey, out var activity);
        if (isNew)
        {
            activity = new DeviceActivityViewModel(
                macKey,
                deviceName,
                ipAddress,
                observation.Domain,
                observation.Sources[0],
                observation.FirstSeen);
            websiteActivityIndex[domainKey] = activity;
            WebsiteActivity.Add(activity);
            group.AddDomain(activity);
        }

        activity!.ObserveBatch(
            deviceName,
            ipAddress,
            observation.Sources,
            observation.FirstSeen,
            observation.LastSeen,
            observation.ObservationCount - (isNew ? 1 : 0),
            observation.LastObservedDrop);
        activity.SetBlocked(policy.ShouldBlockDomain(macKey, activity.Domain));
        activity.SetControlAvailable(device?.CanControl == true);
        group.TouchDomain(activity);
    }

    private void TrimWebsiteActivity()
    {
        foreach (var oldest in WebsiteActivity
                     .OrderBy(activity => activity.LastSeen)
                     .Take(Math.Max(0, WebsiteActivity.Count - MaxWebsiteActivityRows))
                     .ToArray())
        {
            WebsiteActivity.Remove(oldest);
            websiteActivityIndex.Remove($"{oldest.MacKey}|{oldest.Domain}");
            if (websiteActivityGroupIndex.TryGetValue(oldest.MacKey, out var group))
            {
                group.RemoveDomain(oldest);
            }
        }
    }

    private async void BlockVisitedDomainButton_OnClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: DeviceActivityViewModel { CanBlock: true } activity })
        {
            await AddDomainBlockAsync(activity.MacKey, activity.Domain);
        }
    }

    private async void AddDomainRuleButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (DomainRuleDeviceSelector.SelectedItem is not DeviceViewModel { CanControl: true } device)
        {
            DomainRuleValidationText.Text = "Choose a saved or observed device.";
            DomainRuleValidationText.Visibility = Visibility.Visible;
            return;
        }

        string domain;
        try
        {
            domain = TrafficPolicy.NormalizeDomain(DomainRuleInput.Text);
        }
        catch (FormatException exception)
        {
            DomainRuleValidationText.Text = exception.Message;
            DomainRuleValidationText.Visibility = Visibility.Visible;
            DomainRuleInput.Focus();
            return;
        }

        DomainRuleValidationText.Visibility = Visibility.Collapsed;
        await AddDomainBlockAsync(device.MacKey, domain);
        DomainRuleInput.Clear();
    }

    private async void ApplyDomainPresetButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (DomainPresetDeviceSelector.SelectedItem is not DeviceViewModel { CanControl: true } device)
        {
            DomainPresetValidationText.Text = "Choose a saved or observed device.";
            DomainPresetValidationText.Visibility = Visibility.Visible;
            return;
        }

        if (DomainPresetSelector.SelectedItem is not DomainBlockPreset preset)
        {
            DomainPresetValidationText.Text = "Choose an app preset.";
            DomainPresetValidationText.Visibility = Visibility.Visible;
            return;
        }

        DomainPresetValidationText.Visibility = Visibility.Collapsed;
        var macKey = TrafficPolicy.NormalizeMac(device.MacKey);
        if (!settings.AppliedDomainPresets.TryGetValue(macKey, out var appliedPresets))
        {
            appliedPresets = [];
            settings.AppliedDomainPresets[macKey] = appliedPresets;
        }

        if (!appliedPresets.Contains(preset.Name, StringComparer.OrdinalIgnoreCase))
        {
            appliedPresets.Add(preset.Name);
            appliedPresets.Sort(StringComparer.OrdinalIgnoreCase);
        }

        await AddDomainBlocksAsync(
            macKey,
            preset.Domains,
            $"Applied the {preset.Name} preset to {device.DisplayName}. " +
            "Reconnect the app so existing sessions close.");
    }

    private async void RemoveDomainRuleButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: DomainRuleViewModel rule })
        {
            await RemoveDomainBlockAsync(rule);
        }
    }

    private async void RemoveDomainPresetButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { Tag: DomainPresetRuleViewModel preset })
        {
            await RemoveDomainPresetAsync(preset);
        }
    }

    private async Task AddDomainBlockAsync(string macAddress, string domain)
    {
        var normalizedDomain = TrafficPolicy.NormalizeDomain(domain);
        await AddDomainBlocksAsync(
            macAddress,
            [normalizedDomain],
            $"Blocked {normalizedDomain} for {ResolveDeviceName(TrafficPolicy.NormalizeMac(macAddress))}. " +
            "New connections are filtered immediately.");
    }

    private async Task AddDomainBlocksAsync(
        string macAddress,
        IEnumerable<string> requestedDomains,
        string status)
    {
        var macKey = TrafficPolicy.NormalizeMac(macAddress);
        if (!dataReady || !deviceIndex.TryGetValue(macKey, out var device) || !device.CanControl)
        {
            DetailStatusText.Text = "IPv4 filtering is unavailable for this device.";
            return;
        }

        if (!settings.BlockedDomains.TryGetValue(macKey, out var domains))
        {
            domains = [];
            settings.BlockedDomains[macKey] = domains;
        }

        foreach (var requested in requestedDomains)
        {
            var normalized = TrafficPolicy.NormalizeDomain(requested);
            if (!domains.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                domains.Add(normalized);
            }
        }

        domains.Sort(StringComparer.OrdinalIgnoreCase);
        policy.SetBlockedDomains(macKey, domains);
        RebuildDomainRulePresentation();
        RefreshBlockedActivityState();
        await SaveDomainRulesAsync(status);
    }

    private async Task RemoveDomainBlockAsync(DomainRuleViewModel rule)
    {
        if (settings.BlockedDomains.TryGetValue(rule.MacKey, out var domains))
        {
            domains.RemoveAll(domain =>
                domain.Equals(rule.Domain, StringComparison.OrdinalIgnoreCase));
            if (domains.Count == 0)
            {
                settings.BlockedDomains.Remove(rule.MacKey);
            }
        }

        policy.SetBlockedDomains(
            rule.MacKey,
            settings.BlockedDomains.TryGetValue(rule.MacKey, out var remaining)
                ? remaining
                : []);
        RebuildDomainRulePresentation();
        RefreshBlockedActivityState();
        await SaveDomainRulesAsync($"Unblocked {rule.Domain} for {rule.DeviceName}.");
    }

    private async Task RemoveDomainPresetAsync(DomainPresetRuleViewModel preset)
    {
        if (settings.AppliedDomainPresets.TryGetValue(preset.MacKey, out var appliedPresets))
        {
            appliedPresets.RemoveAll(name =>
                name.Equals(preset.PresetName, StringComparison.OrdinalIgnoreCase));
            if (appliedPresets.Count == 0)
            {
                settings.AppliedDomainPresets.Remove(preset.MacKey);
            }
        }

        var domainsStillClaimed = settings.AppliedDomainPresets
            .GetValueOrDefault(preset.MacKey, [])
            .SelectMany(name => DomainBlockPresetCatalog.All
                .Where(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .SelectMany(candidate => candidate.Domains))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (settings.BlockedDomains.TryGetValue(preset.MacKey, out var blockedDomains))
        {
            blockedDomains.RemoveAll(domain =>
                preset.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase) &&
                !domainsStillClaimed.Contains(domain));
            if (blockedDomains.Count == 0)
            {
                settings.BlockedDomains.Remove(preset.MacKey);
            }
        }

        policy.SetBlockedDomains(
            preset.MacKey,
            settings.BlockedDomains.GetValueOrDefault(preset.MacKey, []));
        RebuildDomainRulePresentation();
        RefreshBlockedActivityState();
        await SaveDomainRulesAsync($"Removed the {preset.PresetName} preset from {preset.DeviceName}.");
    }

    private async Task SaveDomainRulesAsync(string status)
    {
        MarkSettingsDirty();
        try
        {
            await SaveSettingsIfDirtyAsync();
            RefreshDomainRulesState();
            DetailStatusText.Text = engine.IsRunning
                ? $"{status} Rules apply only to observed IPv4 traffic."
                : "IPv4 rule saved; monitoring is stopped.";
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportSettingsSaveFailure(exception);
        }
    }

    private void LoadDomainRulesFromSettings()
    {
        foreach (var pair in settings.BlockedDomains)
        {
            policy.SetBlockedDomains(pair.Key, pair.Value);
        }

        RebuildDomainRulePresentation();
        RefreshBlockedActivityState();
        RefreshDomainRulesState();
    }

    private void RebuildDomainRulePresentation()
    {
        DomainPresetRules.Clear();
        DomainRules.Clear();
        foreach (var pair in settings.BlockedDomains)
        {
            var presentation = DomainRulePresentationBuilder.Build(
                pair.Key,
                ResolveDeviceName(pair.Key),
                pair.Value,
                settings.AppliedDomainPresets.GetValueOrDefault(pair.Key, []));
            foreach (var preset in presentation.Presets)
            {
                DomainPresetRules.Add(preset);
            }

            foreach (var rule in presentation.IndividualRules)
            {
                DomainRules.Add(rule);
            }
        }
    }

    private string ResolveDeviceName(string macKey)
    {
        if (deviceIndex.TryGetValue(macKey, out var device))
        {
            return device.DisplayName;
        }

        if (settings.Devices.TryGetValue(macKey, out var preferences))
        {
            if (!string.IsNullOrWhiteSpace(preferences.Alias))
            {
                return preferences.Alias;
            }

            if (!string.IsNullOrWhiteSpace(preferences.LearnedHostName))
            {
                return preferences.LearnedHostName;
            }
        }

        return "Unknown device";
    }

    private void RefreshBlockedActivityState()
    {
        foreach (var activity in WebsiteActivity)
        {
            activity.SetBlocked(policy.ShouldBlockDomain(activity.MacKey, activity.Domain));
        }
    }

    private void RefreshDomainRulesState()
    {
        DomainRulesEmptyState.Visibility = DomainRules.Count == 0 && DomainPresetRules.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        var presetLabel = $"{DomainPresetRules.Count} preset{(DomainPresetRules.Count == 1 ? string.Empty : "s")}";
        var ruleLabel = $"{DomainRules.Count} rule{(DomainRules.Count == 1 ? string.Empty : "s")}";
        DomainRuleCountText.Text = $"{presetLabel}  •  {ruleLabel}";
        CollectionViewSource.GetDefaultView(DomainPresetRules).Refresh();
        CollectionViewSource.GetDefaultView(DomainRules).Refresh();
    }

    private void RefreshWebsiteActivityState()
    {
        WebsiteActivityEmptyState.Visibility = DeviceActivityGroups.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        WebsiteActivityCountText.Text =
            $"{DeviceActivityGroups.Count} device group{(DeviceActivityGroups.Count == 1 ? string.Empty : "s")}  •  " +
            $"{WebsiteActivity.Count}/{MaxWebsiteActivityRows} retained device/domain pairs — session only" +
            (omittedDomainObservations > 0
                ? $"  •  {omittedDomainObservations:N0} observations omitted (buffer full)"
                : string.Empty);
    }

    private DeviceActivityGroupViewModel GetOrCreateWebsiteActivityGroup(
        string macKey,
        string deviceName,
        string ipAddress)
    {
        if (!websiteActivityGroupIndex.TryGetValue(macKey, out var group))
        {
            group = new DeviceActivityGroupViewModel(macKey, deviceName, ipAddress);
            websiteActivityGroupIndex[macKey] = group;
        }
        else
        {
            group.UpdateIdentity(deviceName, ipAddress);
        }

        if (!DeviceActivityGroups.Contains(group))
        {
            DeviceActivityGroups.Add(group);
        }

        return group;
    }

    private void SyncWebsiteActivityGroups()
    {
        foreach (var group in DeviceActivityGroups.ToArray())
        {
            if (deviceIndex.TryGetValue(group.MacKey, out var device))
            {
                group.UpdateIdentity(device.DisplayName, device.IpAddress);
                foreach (var activity in group.Domains)
                {
                    activity.ObserveBatch(
                        device.DisplayName,
                        device.IpAddress,
                        [],
                        activity.FirstSeen,
                        activity.LastSeen,
                        0,
                        activity.LastObservedDrop);
                    activity.SetControlAvailable(device.CanControl);
                }
            }

            if (!group.HasDomains)
            {
                DeviceActivityGroups.Remove(group);
                websiteActivityGroupIndex.Remove(group.MacKey);
            }
        }

        RefreshWebsiteActivityState();
    }

    private void SyncDomainRuleDevices(bool refreshOrdering = false)
    {
        var available = Devices.Where(device => device.CanControl).ToHashSet();
        foreach (var stale in DomainRuleDevices.Where(device => !available.Contains(device)).ToArray())
        {
            DomainRuleDevices.Remove(stale);
        }

        foreach (var device in available)
        {
            if (!DomainRuleDevices.Contains(device))
            {
                DomainRuleDevices.Add(device);
            }
        }

        foreach (var rule in DomainRules)
        {
            rule.UpdateDeviceName(ResolveDeviceName(rule.MacKey));
        }

        foreach (var preset in DomainPresetRules)
        {
            preset.UpdateDeviceName(ResolveDeviceName(preset.MacKey));
        }

        if (DomainRuleDeviceSelector.SelectedItem is null && DomainRuleDevices.Count > 0)
        {
            DomainRuleDeviceSelector.SelectedIndex = 0;
        }

        if (DomainPresetDeviceSelector.SelectedItem is null && DomainRuleDevices.Count > 0)
        {
            DomainPresetDeviceSelector.SelectedIndex = 0;
        }

        if (refreshOrdering)
        {
            CollectionViewSource.GetDefaultView(DomainRuleDevices).Refresh();
            CollectionViewSource.GetDefaultView(DomainPresetRules).Refresh();
            CollectionViewSource.GetDefaultView(DomainRules).Refresh();
            CollectionViewSource.GetDefaultView(DeviceActivityGroups).Refresh();
        }
    }

    private void AdapterSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        UpdateAdapterSummary();
        if (engine is null)
        {
            return;
        }

        if (!engine.IsRunning && AdapterSelector.SelectedItem is AdapterProfile selected)
        {
            registry.Remember(selected.LocalAddress, selected.LocalMac, DateTimeOffset.MinValue, Dns.GetHostName());
            registry.SetHostName(selected.LocalMac, Dns.GetHostName());
            RefreshDevices(null, EventArgs.Empty);
        }

        UpdateButtons();
    }

    private void BeginOperation()
    {
        operationInProgress = true;
        operationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        UpdateButtons();
    }

    private void CompleteOperation()
    {
        operationInProgress = false;
        var completion = operationCompletion;
        operationCompletion = null;
        UpdateButtons();
        completion?.TrySetResult(true);
    }

    private async Task StopEngineAsync()
    {
        if (operationInProgress || !engine.IsRunning)
        {
            return;
        }

        BeginOperation();
        refreshTimer.Stop();
        SetStatus("Attempting to restore normal ARP mappings…", null);
        try
        {
            await engine.StopAsync();
            registry.BeginSession();
            RefreshDevices(null, EventArgs.Empty);
            DrainDomainObservations(null, EventArgs.Empty);
            controlStartedAt = null;
            UptimeText.Text = "Idle";
            SetStatus(engine.LastStopStatus, null);
            try
            {
                await usageTracker.FlushAndSaveAsync();
                usageSaveWarning = null;
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            {
                usageSaveWarning = $"Could not save usage: {exception.Message}";
            }

            await TrySaveSettingsAsync();
            RefreshCaptureDiagnostics();
        }
        finally
        {
            CompleteOperation();
        }
    }

    private void RefreshDevices(object? sender, EventArgs eventArgs)
    {
        var activeProfile = AdapterSelector.SelectedItem as AdapterProfile;
        var now = DateTimeOffset.UtcNow;
        var view = CollectionViewSource.GetDefaultView(Devices);
        var editableView = view as IEditableCollectionView;
        var canMutateView = DeviceListRefreshPolicy.ShouldRefresh(
            Keyboard.FocusedElement is TextBox,
            editableView?.IsAddingNew == true,
            editableView?.IsEditingItem == true);
        foreach (var snapshot in registry.TakeSnapshot(now))
        {
            var key = snapshot.MacAddress.ToString();
            settings.Devices.TryGetValue(key, out var preferences);
            var isLocalComputer = activeProfile is not null && snapshot.MacAddress.Equals(activeProfile.LocalMac);
            var isGateway = !isLocalComputer && engine.IsGatewayMac(snapshot.MacAddress);
            var protectedReason = isLocalComputer
                ? "This computer — protected"
                : isGateway ? "Gateway — protected" : string.Empty;
            if (dataReady && !isGateway)
            {
                preferences ??= settings.Devices[key] = new DevicePreferences();
                UpdateRememberedMetadata(snapshot, preferences, isLocalComputer);
            }

            if (!deviceIndex.TryGetValue(key, out var viewModel))
            {
                if (!canMutateView)
                {
                    continue;
                }

                viewModel = new DeviceViewModel(OnDeviceRuleChangedAsync);
                viewModel.Initialize(snapshot, preferences, isLocalComputer || isGateway, protectedReason);
                deviceIndex[key] = viewModel;
                Devices.Add(viewModel);
                deviceOrderingDirty = true;
            }

            var previousName = viewModel.DisplayName;
            var previouslyProtected = viewModel.IsProtected;
            viewModel.SetProtection(isLocalComputer || isGateway, protectedReason);
            viewModel.SetMonitoring(engine.IsRunning);
            viewModel.Update(snapshot, preferences?.Alias);
            viewModel.SetPresence(
                engine.IsRunning && !snapshot.IsRemembered &&
                DevicePresencePolicy.Classify(snapshot.LastSeen, now) == DevicePresence.Online);
            var coverage = engine.GetCoverage(snapshot.MacAddress);
            viewModel.SetCaptureCoverage(isGateway
                ? "Gateway — protected and not metered as a client. Saved values, if any, are historical observations."
                : $"{(engine.IsRunning ? "Current" : "Last")} capture session: " +
                  $"↑ {coverage.UploadPackets:N0} packets ({DeviceViewModel.FormatExactBytes(coverage.UploadPayloadBytes)} payload), " +
                  $"↓ {coverage.DownloadPackets:N0} packets ({DeviceViewModel.FormatExactBytes(coverage.DownloadPayloadBytes)} payload). " +
                  coverage.Status);
            deviceOrderingDirty |= previouslyProtected != viewModel.IsProtected ||
                                   !string.Equals(previousName, viewModel.DisplayName, StringComparison.Ordinal);
        }

        var refreshOrdering = canMutateView && deviceOrderingDirty;
        if (refreshOrdering)
        {
            view.Refresh();
            deviceOrderingDirty = false;
        }

        SyncWebsiteActivityGroups();
        SyncDomainRuleDevices(refreshOrdering);
        RefreshDashboardSummary();
        RefreshCaptureDiagnostics();
        EmptyState.Visibility = Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeviceCountText.Text = $"{Devices.Count} saved or observed device{(Devices.Count == 1 ? string.Empty : "s")}";
    }

    private void UpdateRememberedMetadata(
        DeviceSnapshot snapshot,
        DevicePreferences preferences,
        bool isLocalComputer)
    {
        var newerObservation = snapshot.LastSeen != DateTimeOffset.MinValue &&
                               (preferences.LastSeenUtc is null || snapshot.LastSeen > preferences.LastSeenUtc);
        if ((!snapshot.IsRemembered || newerObservation || isLocalComputer) &&
            snapshot.IpAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
            !snapshot.IpAddress.Equals(IPAddress.Any))
        {
            var currentIp = snapshot.IpAddress.ToString();
            if (!string.Equals(preferences.LastKnownIp, currentIp, StringComparison.Ordinal))
            {
                preferences.LastKnownIp = currentIp;
                MarkSettingsDirty();
            }
        }

        if (newerObservation)
        {
            preferences.LastSeenUtc = snapshot.LastSeen.ToUniversalTime();
            MarkSettingsDirty();
        }

        if (isLocalComputer && !string.Equals(preferences.LearnedHostName, snapshot.HostName, StringComparison.Ordinal))
        {
            preferences.LearnedHostName = snapshot.HostName;
            MarkSettingsDirty();
        }
    }

    private async Task OnDeviceRuleChangedAsync(DeviceViewModel device)
    {
        if (!dataReady || closingInProgress || !device.CanRename)
        {
            return;
        }

        var preferences = settings.Devices.TryGetValue(device.MacKey, out var existing)
            ? existing
            : settings.Devices[device.MacKey] = new DevicePreferences();
        var rule = new TrafficRule(device.PauseInternet, device.DownloadLimit, device.UploadLimit)
            .ForForwardingMode();
        var ruleChanged = device.CanControl &&
            (preferences.DownloadKiloBytesPerSecond != rule.DownloadKiloBytesPerSecond ||
             preferences.UploadKiloBytesPerSecond != rule.UploadKiloBytesPerSecond ||
             preferences.PauseInternet != rule.PauseInternet);
        var aliasChanged = !string.Equals(preferences.Alias, device.Alias, StringComparison.Ordinal);
        if (!ruleChanged && !aliasChanged)
        {
            return;
        }

        if (device.CanControl)
        {
            preferences.DownloadKiloBytesPerSecond = rule.DownloadKiloBytesPerSecond;
            preferences.UploadKiloBytesPerSecond = rule.UploadKiloBytesPerSecond;
            preferences.PauseInternet = rule.PauseInternet;
        }

        preferences.Alias = device.Alias;
        MarkSettingsDirty();
        deviceOrderingDirty |= aliasChanged;
        SyncWebsiteActivityGroups();
        SyncDomainRuleDevices();
        string? ruleWarning = null;
        if (ruleChanged)
        {
            try
            {
                await engine.ApplyRuleAsync(device.MacKey, rule);
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException)
            {
                ruleWarning = $"Could not confirm IPv4 policy activation: {exception.Message}";
            }
        }

        try
        {
            await SaveSettingsIfDirtyAsync();
            DetailStatusText.Text = ruleWarning ??
                (aliasChanged && !ruleChanged
                    ? $"Device name saved: {device.DisplayName}."
                    : engine.IsRunning
                        ? $"IPv4 policy configured for {device.DisplayName}; unobserved traffic is outside its scope."
                        : $"IPv4 policy saved for {device.DisplayName}; monitoring is stopped.");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportSettingsSaveFailure(exception);
        }

        RefreshDashboardSummary(addTrafficSample: false);
    }

    private void ApplyAllSavedRules()
    {
        var localMac = (AdapterSelector.SelectedItem as AdapterProfile)?.LocalMac.ToString();
        foreach (var pair in settings.Devices)
        {
            if (string.Equals(pair.Key, localMac, StringComparison.OrdinalIgnoreCase))
            {
                policy.RemoveRule(pair.Key);
                continue;
            }

            policy.SetRule(
                pair.Key,
                new TrafficRule(
                    pair.Value.PauseInternet,
                    pair.Value.DownloadKiloBytesPerSecond,
                    pair.Value.UploadKiloBytesPerSecond)
                    .ForForwardingMode());
        }

        foreach (var pair in settings.BlockedDomains)
        {
            policy.SetBlockedDomains(pair.Key, pair.Value);
        }
    }

    private void UpdateKnownDeviceHints()
    {
        engine.ReplaceKnownDeviceHints(KnownDeviceHintFactory.Build(settings));
    }

    private void MarkSettingsDirty() => settingsRevision++;

    private async Task SaveSettingsIfDirtyAsync()
    {
        if (!dataReady || settingsRevision == savedSettingsRevision)
        {
            return;
        }

        await settingsSaveSync.WaitAsync();
        try
        {
            if (settingsRevision == savedSettingsRevision)
            {
                return;
            }

            var revision = settingsRevision;
            await settingsStore.SaveAsync(settings);
            savedSettingsRevision = revision;
            settingsSaveWarning = null;
            RefreshCaptureDiagnostics();
        }
        finally
        {
            settingsSaveSync.Release();
        }
    }

    private async Task TrySaveSettingsAsync()
    {
        try
        {
            await SaveSettingsIfDirtyAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportSettingsSaveFailure(exception);
        }
    }

    private void ReportSettingsSaveFailure(Exception exception)
    {
        settingsSaveWarning = $"Could not save device settings: {exception.Message}";
        DetailStatusText.Text = settingsSaveWarning;
        RefreshCaptureDiagnostics();
    }

    private void Engine_OnStatusChanged(object? sender, string message)
    {
        _ = Dispatcher.InvokeAsync(() => DetailStatusText.Text = message);
    }

    private void Engine_OnStateChanged(
        object? sender,
        PcapEngineStateChangedEventArgs eventArgs)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            ApplyEngineState(eventArgs.IsRunning);
            DetailStatusText.Text = eventArgs.StatusMessage;
        });
    }

    private void ApplyEngineState(bool isRunning)
    {
        if (isRunning)
        {
            controlStartedAt ??= DateTimeOffset.UtcNow;
            refreshTimer.Start();
            TopStatusText.Text = "Active";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(104, 192, 138));
        }
        else
        {
            refreshTimer.Stop();
            controlStartedAt = null;
            UptimeText.Text = "Idle";
            TopStatusText.Text = "Idle";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(168, 144, 149));
        }

        foreach (var device in Devices)
        {
            device.SetMonitoring(isRunning);
            if (!isRunning)
            {
                device.SetPresence(false);
            }
        }

        UpdateButtons();
        RefreshDashboardSummary(addTrafficSample: false);
        RefreshCaptureDiagnostics();
    }

    private void Engine_OnDeviceIdentityLearned(
        object? sender,
        DeviceIdentityLearnedEventArgs eventArgs)
    {
        _ = Dispatcher
            .InvokeAsync(() => PersistLearnedIdentityAsync(eventArgs))
            .Task
            .Unwrap();
    }

    private async Task PersistLearnedIdentityAsync(
        DeviceIdentityLearnedEventArgs eventArgs)
    {
        await identitySaveSync.WaitAsync();
        try
        {
            if (!dataReady || closingInProgress || !engine.IsIdentityCurrent(eventArgs))
            {
                return;
            }

            DeviceIdentityTracker.Learn(
                settings,
                eventArgs.MacAddress.ToString(),
                eventArgs.HostName,
                eventArgs.Address?.ToString());
            MarkSettingsDirty();
            UpdateKnownDeviceHints();
            await SaveSettingsIfDirtyAsync();
            DetailStatusText.Text = $"Automatic name remembered: {eventArgs.HostName}. Manual names and policies are unchanged.";
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportSettingsSaveFailure(exception);
        }
        finally
        {
            identitySaveSync.Release();
        }
    }

    private async void MainWindow_OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (closeAfterStop)
        {
            SaveUsageNow();
            return;
        }

        if (!engine.IsRunning && usagePersistence is null && !dataReady && !operationInProgress)
        {
            closingInProgress = true;
            refreshTimer.Stop();
            domainRefreshTimer.Stop();
            settingsSaveTimer.Stop();
            return;
        }

        eventArgs.Cancel = true;
        if (closingInProgress)
        {
            return;
        }

        CommitPendingDeviceEdits();
        closingInProgress = true;
        IsEnabled = false;
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            await (operationCompletion?.Task ?? Task.CompletedTask);
            await StopEngineAsync();
            refreshTimer.Stop();
            domainRefreshTimer.Stop();
            settingsSaveTimer.Stop();
            if (usagePersistence is not null)
            {
                await usagePersistence.StopAsync();
            }

            await SaveSettingsIfDirtyAsync();
            if (dataReady)
            {
                await usageTracker.FlushAndSaveAsync();
                usageSaveWarning = null;
            }

            await engine.DisposeAsync();
            if (usagePersistence is { } persistence)
            {
                usagePersistence = null;
                await persistence.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            var exit = MessageBox.Show(
                $"Closing could not finish normally: {exception.Message}\n\n" +
                "The last successful saved files are retained. Close anyway? " +
                "Choose No to keep the app open and retry saving or Stop & restore.",
                "Usage Meter",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (exit != MessageBoxResult.Yes)
            {
                closingInProgress = false;
                IsEnabled = true;
                if (dataReady)
                {
                    StartUsagePersistence();
                    domainRefreshTimer.Start();
                    settingsSaveTimer.Start();
                    if (engine.IsRunning)
                    {
                        refreshTimer.Start();
                    }
                }

                SetStatus(exception.Message, false);
                UpdateButtons();
                return;
            }

            try
            {
                await engine.DisposeAsync();
            }
            catch (Exception)
            {
                // The user explicitly chose to close despite the cleanup failure.
            }

            if (usagePersistence is { } persistence)
            {
                usagePersistence = null;
                try
                {
                    await persistence.DisposeAsync();
                }
                catch (Exception)
                {
                    // Forced close must not strand a disabled window.
                }
            }
        }

        closeAfterStop = true;
        Close();
    }

    private void CommitPendingDeviceEdits()
    {
        DeviceNameEditor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        DownloadLimitEditor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        UploadLimitEditor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void UpdateButtons()
    {
        StartButton.IsEnabled = dataReady && !closingInProgress && !operationInProgress &&
                                !engine.IsRunning &&
                                AdapterSelector.SelectedItem is AdapterProfile;
        StopButton.IsEnabled = !closingInProgress && !operationInProgress && engine.IsRunning;
        ScanButton.IsEnabled = !closingInProgress && !operationInProgress && engine.IsRunning;
        AdapterSelector.IsEnabled = !closingInProgress && !operationInProgress && !engine.IsRunning;
        StartButton.Visibility = engine.IsRunning ? Visibility.Collapsed : Visibility.Visible;
        ScanButton.Visibility = engine.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = engine.IsRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MinimizeWindowButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindowButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseWindowButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        Close();
    }

    private void NavigationButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: string targetName } ||
            FindName(targetName) is not FrameworkElement target)
        {
            return;
        }

        var pageName = target == AdapterStrip ? OverviewSection.Name : target.Name;
        if (!string.Equals(currentPageName, pageName, StringComparison.Ordinal))
        {
            pageScrollOffsets[currentPageName] = MainScrollViewer.VerticalOffset;
            currentPageName = pageName;
            SetVisiblePage(target);
            var offset = pageScrollOffsets.GetValueOrDefault(pageName);
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (string.Equals(currentPageName, pageName, StringComparison.Ordinal))
                {
                    MainScrollViewer.ScrollToVerticalOffset(offset);
                }
            }, DispatcherPriority.Loaded);
        }

        if (target == AdapterStrip)
        {
            AdapterSelector.Focus();
            AdapterStrip.BringIntoView();
        }
    }

    private void SetVisiblePage(FrameworkElement target)
    {
        var showOverview = target == OverviewSection || target == AdapterStrip;
        var overviewVisibility = showOverview ? Visibility.Visible : Visibility.Collapsed;
        OverviewSection.Visibility = overviewVisibility;
        AdapterStrip.Visibility = overviewVisibility;
        MetricsPanel.Visibility = overviewVisibility;
        ActivitySection.Visibility = overviewVisibility;
        DeviceSection.Visibility = overviewVisibility;
        WebsiteActivitySection.Visibility = target == WebsiteActivitySection
            ? Visibility.Visible
            : Visibility.Collapsed;
        DomainRulesSection.Visibility = target == DomainRulesSection
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateAdapterSummary()
    {
        if (AdapterSelector.SelectedItem is not AdapterProfile selected)
        {
            LocalIpText.Text = "-";
            GatewayIpText.Text = "-";
            return;
        }

        LocalIpText.Text = selected.LocalAddress.ToString();
        GatewayIpText.Text = selected.GatewayAddress.ToString();
    }

    private void RefreshDashboardSummary(bool addTrafficSample = true)
    {
        var summary = DashboardSummary.From(Devices);
        ConnectedDevicesMetric.Text = summary.ConnectedDevices.ToString();
        DownloadMetric.Text = engine.IsRunning ? DeviceViewModel.FormatRate(summary.DownloadBytesPerSecond) : "—";
        UploadMetric.Text = engine.IsRunning ? DeviceViewModel.FormatRate(summary.UploadBytesPerSecond) : "—";
        ActiveRulesMetric.Text = summary.ActiveRules.ToString();

        if (addTrafficSample && engine.IsRunning)
        {
            var sample = summary.ToTrafficSample(DateTimeOffset.UtcNow);
            if (trafficHistory.TryAdd(sample, TrafficSamplingProfile.Interval))
            {
                TrafficChart.Samples = trafficHistory.Samples;
                ChartDeviceSummaryText.Text = TrafficChartPresentation.BuildLatestSummary(sample);
            }
        }

        if (controlStartedAt is { } startedAt)
        {
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            UptimeText.Text = $"Active {elapsed:hh\\:mm\\:ss}";
        }
    }

    private void RefreshCaptureDiagnostics()
    {
        var warnings = new[] { loadWarning, usageSaveWarning, settingsSaveWarning }
            .Where(message => !string.IsNullOrWhiteSpace(message));
        var warningText = string.Join("  ", warnings);
        var omissionText = omittedDomainObservations > 0
            ? $"Domain display buffer omitted {omittedDomainObservations:N0} observations; usage counters are independent.  "
            : string.Empty;
        SavedDataWarningText.Text = warningText.Length > 0 ? $"Saved-data warning: {warningText}" : string.Empty;
        SavedDataWarningText.Visibility = warningText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CaptureDiagnosticsText.Text = omissionText + engine.GetDiagnosticsSummary();
    }

    private void SetStatus(string message, bool? active)
    {
        TopStatusText.Text = active switch
        {
            true => "Active",
            false => "Needs attention",
            null => engine.IsRunning ? "Working" : "Idle",
        };
        DetailStatusText.Text = message;
        StatusDot.Fill = new SolidColorBrush(
            active switch
            {
                true => Color.FromRgb(104, 192, 138),
                false => Color.FromRgb(240, 100, 115),
                null => Color.FromRgb(168, 144, 149),
            });
    }
}
