using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shell;
using Lantern.App;
using Lantern.App.ViewModels;
using Lantern.Core.Devices;
using Lantern.Core.Settings;
using Lantern.Core.Usage;
using Xunit;

namespace Lantern.App.Tests;

[Collection("WPF application")]
public sealed class DeviceViewModelTests
{
    private readonly WpfApplicationFixture fixture;

    public DeviceViewModelTests(WpfApplicationFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public void ApplicationIcon_ContainsWindowsIconFramesForSmallAndLargeSizes()
    {
        var iconPath = Path.Combine(
            GetProjectRoot(),
            "src",
            "Lantern.App",
            "Assets",
            "RedWatcher.ico");

        Assert.True(File.Exists(iconPath), $"Missing application icon: {iconPath}");
        var bytes = File.ReadAllBytes(iconPath);
        Assert.True(bytes.Length > 6);
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, bytes[..4]);
        Assert.Equal(7, BitConverter.ToUInt16(bytes, 4));
    }

    [Fact]
    public void MainWindow_LoadsBrandingChartAndUsableLimitEditors()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opencode", $"UsageMeter-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            fixture.Run(() =>
            {
                var app = Assert.IsAssignableFrom<App>(System.Windows.Application.Current);

                SolidColorBrush Brush(string key) =>
                    Assert.IsType<SolidColorBrush>(app.Resources[key]);
                var background = Brush("WindowBackground").Color;
                var primaryText = Brush("PrimaryText").Color;
                var secondaryText = Brush("SecondaryText").Color;
                var accent = Brush("Accent").Color;
                var download = Brush("DownloadAccent").Color;
                var upload = Brush("UploadAccent").Color;
                var success = Brush("Success").Color;

                Assert.True(
                    Math.Max(background.R, Math.Max(background.G, background.B)) -
                    Math.Min(background.R, Math.Min(background.G, background.B)) <= 4,
                    "The main canvas must remain neutral near-black rather than blue-tinted.");
                Assert.True(
                    accent.R > accent.G * 2 && accent.R > accent.B * 2,
                    "The brand accent must be restrained crimson rather than teal or blue.");
                Assert.True(RelativeLuminance(accent) < 0.25);
                Assert.True(ContrastRatio(primaryText, background) >= 7);
                Assert.True(ContrastRatio(secondaryText, background) >= 4.5);
                Assert.Equal(accent, download);
                Assert.True(upload.R > upload.B && upload.R > upload.G);
                Assert.True(success.G > success.R && success.G > success.B);

                var scrollBarStyle = Assert.IsType<Style>(app.Resources[typeof(ScrollBar)]);
                Assert.Contains(
                    scrollBarStyle.Setters,
                    setter => setter is Setter { Property: var property } &&
                              property == Control.TemplateProperty);

                var window = new MainWindow(new SettingsStore(directory), new UsageStore(directory), () => []);
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                var windowChrome = Assert.IsType<WindowChrome>(
                    WindowChrome.GetWindowChrome(window));
                Assert.True(windowChrome.CaptionHeight >= 38);
                Assert.True(windowChrome.ResizeBorderThickness.Left >= 6);
                foreach (var name in new[]
                {
                        "CustomTitleBar",
                        "MinimizeWindowButton",
                        "MaximizeWindowButton",
                        "CloseWindowButton",
                        "Sidebar",
                        "SidebarVersionText",
                        "BrandLogo",
                        "OverviewNavButton",
                        "ActivityNavButton",
                        "DomainRulesNavButton",
                        "AdapterStrip",
                        "MetricsPanel",
                        "ConnectedDevicesMetric",
                        "DownloadMetric",
                        "UploadMetric",
                        "ActiveRulesMetric",
                        "ActivitySection",
                        "WebsiteActivitySection",
                        "WebsiteActivityDeviceList",
                        "ClearActivityButton",
                        "DomainRulesSection",
                        "DomainPresetDeviceSelector",
                        "DomainPresetSelector",
                        "ApplyDomainPresetButton",
                        "DomainRuleDeviceSelector",
                        "DomainRuleInput",
                        "AddDomainRuleButton",
                        "DomainPresetRulesList",
                        "DomainRulesList",
                        "DomainRulesEmptyState",
                        "ChartDeviceSummaryText",
                        "DeviceSection",
                        "DeviceGrid",
                        "SelectedDeviceDetails",
                        "MonitoringStatusStrip",
                        "DetailStatusText",
                        "CaptureDiagnosticsText",
                        "CaptureDiagnosticsExpander",
                        "CaptureScopeNoticeText",
                        "SavedDataWarningText",
                        "DeviceCaptureCoverageText",
                    })
                {
                    Assert.NotNull(window.FindName(name));
                }

                var sidebarVersion = Assert.IsType<TextBlock>(
                    window.FindName("SidebarVersionText"));
                Assert.Equal("v1.0.1", sidebarVersion.Text);
                Assert.Equal("Usage Meter", window.Title);

                var minimizeButton = Assert.IsType<Button>(
                    window.FindName("MinimizeWindowButton"));
                minimizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(WindowState.Minimized, window.WindowState);
                window.WindowState = WindowState.Normal;

                var maximizeButton = Assert.IsType<Button>(
                    window.FindName("MaximizeWindowButton"));
                maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(WindowState.Maximized, window.WindowState);
                maximizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(WindowState.Normal, window.WindowState);

                Assert.Null(window.FindName("AdapterNavButton"));

                var adapterStrip = Assert.IsType<Border>(window.FindName("AdapterStrip"));
                var activitySection = Assert.IsType<Border>(
                    window.FindName("ActivitySection"));
                Assert.Equal(
                    activitySection.Margin.Left,
                    adapterStrip.Margin.Left);

                var adapterSelector = Assert.IsType<ComboBox>(
                    window.FindName("AdapterSelector"));
                adapterSelector.ApplyTemplate();
                var dropDownToggle = Assert.IsType<ToggleButton>(
                    adapterSelector.Template.FindName("DropDownToggle", adapterSelector));
                dropDownToggle.ApplyTemplate();
                var comboBorder = Assert.IsType<Border>(
                    dropDownToggle.Template.FindName("ComboBorder", dropDownToggle));
                Assert.True(
                    comboBorder.Padding.Left >= 12,
                    "The selected adapter text needs a visible inset from its input border. " +
                    $"Selector={adapterSelector.Padding.Left}, " +
                    $"Toggle={dropDownToggle.Padding.Left}, " +
                    $"Border={comboBorder.Padding.Left}.");

                var localIpBlock = Assert.IsType<StackPanel>(
                    window.FindName("LocalIpBlock"));
                Assert.True(
                    localIpBlock.Margin.Left >= 16,
                    "The Local IP block needs a visible inset from its column edge.");

                var websiteDeviceList = Assert.IsType<ItemsControl>(
                    window.FindName("WebsiteActivityDeviceList"));
                var activityItem = Assert.IsType<Border>(
                    websiteDeviceList.ItemTemplate.LoadContent());
                var deviceExpander = Assert.IsType<Expander>(activityItem.Child);
                var expandedBinding = Assert.IsType<Binding>(
                    BindingOperations.GetBinding(
                        deviceExpander,
                        Expander.IsExpandedProperty));
                Assert.Equal("IsExpanded", expandedBinding.Path.Path);
                Assert.Equal(BindingMode.TwoWay, expandedBinding.Mode);

                var overviewSection = Assert.IsType<Grid>(window.FindName("OverviewSection"));
                var websiteSection = Assert.IsType<Border>(
                    window.FindName("WebsiteActivitySection"));
                var domainRulesSection = Assert.IsType<Border>(
                    window.FindName("DomainRulesSection"));
                Assert.Equal(Visibility.Visible, overviewSection.Visibility);
                Assert.Equal(Visibility.Collapsed, websiteSection.Visibility);
                Assert.Equal(Visibility.Collapsed, domainRulesSection.Visibility);

                var activityNav = Assert.IsType<Button>(window.FindName("ActivityNavButton"));
                activityNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Collapsed, overviewSection.Visibility);
                Assert.Equal(Visibility.Visible, websiteSection.Visibility);

                var overviewNav = Assert.IsType<Button>(window.FindName("OverviewNavButton"));
                overviewNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Visible, overviewSection.Visibility);
                Assert.Equal(Visibility.Collapsed, websiteSection.Visibility);

                var domainRulesNav = Assert.IsType<Button>(
                    window.FindName("DomainRulesNavButton"));
                domainRulesNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Collapsed, overviewSection.Visibility);
                Assert.Equal(Visibility.Collapsed, websiteSection.Visibility);
                Assert.Equal(Visibility.Visible, domainRulesSection.Visibility);

                var presetSelector = Assert.IsType<ComboBox>(
                    window.FindName("DomainPresetSelector"));
                var presetNames = presetSelector.Items.Cast<object>()
                    .Select(item =>
                        item.GetType().GetProperty("Name")?.GetValue(item) as string ??
                        string.Empty)
                    .ToArray();
                Assert.Equal(
                    ["YouTube", "Instagram", "Facebook", "Snapchat", "Discord", "Messenger"],
                    presetNames);

                var brandLogo = Assert.IsType<Lantern.App.Controls.RedWatcherLogo>(
                    window.FindName("BrandLogo"));
                Assert.Equal(
                    "Usage Meter logo",
                    System.Windows.Automation.AutomationProperties.GetName(brandLogo));
                var pupil = Assert.IsType<System.Windows.Shapes.Ellipse>(
                    brandLogo.FindName("RedWatcherPupil"));
                Assert.Equal(
                    System.Windows.Media.Color.FromRgb(2, 2, 3),
                    Assert.IsType<System.Windows.Media.SolidColorBrush>(pupil.Fill).Color);
                Assert.Null(brandLogo.FindName("RedWatcherCatchlight"));
                Assert.True(
                    brandLogo.Width >= 48,
                    "The in-app brand mark must use enough of its badge to preserve eye detail.");
                var irisTexture = Assert.IsType<System.Windows.Shapes.Path>(
                    brandLogo.FindName("RedWatcherIrisTexture"));
                Assert.True(
                    irisTexture.Data.Bounds.Width >= 55,
                    "The Red Watcher iris must include visible vector texture rather than a flat dot.");
                Assert.IsType<System.Windows.Shapes.Ellipse>(
                    brandLogo.FindName("RedWatcherInnerIrisRing"));

                var chart = Assert.IsType<Lantern.App.Controls.LiveTrafficChart>(
                    window.FindName("TrafficChart"));
                Assert.Equal(TimeSpan.FromMinutes(10), chart.VisibleDuration);
                Assert.Equal(238, chart.Height);
                Assert.True(
                    chart.ClipToBounds,
                    "The traffic chart must clip strokes and markers inside its own drawing surface.");
                var deviceGrid = Assert.IsType<DataGrid>(window.FindName("DeviceGrid"));
                Assert.Equal(68, deviceGrid.RowHeight);
                Assert.True(deviceGrid.IsReadOnly);
                Assert.Equal(
                    new[] { "DEVICE", "IP / PRESENCE", "DOWN NOW", "UP NOW", "MEASURED", "POLICY" },
                    deviceGrid.Columns.Select(column => column.Header));
                var deviceColumn = Assert.IsType<DataGridTemplateColumn>(deviceGrid.Columns[0]);
                var deviceIdentity = Assert.IsType<Grid>(deviceColumn.CellTemplate!.LoadContent());
                Assert.Equal(new GridLength(36), deviceIdentity.ColumnDefinitions[0].Width);
                Assert.Null(deviceIdentity.FindName("DeviceNameEditor"));
                Assert.Null(deviceIdentity.FindName("DeviceMacAddress"));
                var details = Assert.IsType<Border>(window.FindName("SelectedDeviceDetails"));
                var selectedBinding = Assert.IsType<Binding>(
                    BindingOperations.GetBinding(details, FrameworkElement.DataContextProperty));
                Assert.Equal("DeviceGrid", selectedBinding.ElementName);
                Assert.Equal("SelectedItem", selectedBinding.Path.Path);
                var coverage = Assert.IsType<TextBlock>(window.FindName("DeviceCaptureCoverageText"));
                Assert.Equal(
                    nameof(DeviceViewModel.CaptureCoverageText),
                    BindingOperations.GetBinding(coverage, TextBlock.TextProperty)?.Path.Path);
                Assert.Equal(TextWrapping.Wrap, coverage.TextWrapping);
                var diagnosticsExpander = Assert.IsType<Expander>(window.FindName("CaptureDiagnosticsExpander"));
                Assert.False(diagnosticsExpander.IsExpanded);
                var savedWarning = Assert.IsType<TextBlock>(window.FindName("SavedDataWarningText"));
                Assert.Equal(Visibility.Collapsed, savedWarning.Visibility);
                savedWarning.Text = "Saved usage could not be read.";
                savedWarning.Visibility = Visibility.Visible;
                Assert.Equal(Visibility.Visible, savedWarning.Visibility);
                Assert.False(diagnosticsExpander.IsExpanded);
                savedWarning.Visibility = Visibility.Collapsed;
                savedWarning.Text = string.Empty;
                var deviceNameEditor = Assert.IsType<TextBox>(window.FindName("DeviceNameEditor"));
                var aliasBinding = Assert.IsType<Binding>(
                    BindingOperations.GetBinding(deviceNameEditor, TextBox.TextProperty));
                Assert.Equal("EditableAlias", aliasBinding.Path.Path);
                Assert.Equal(UpdateSourceTrigger.LostFocus, aliasBinding.UpdateSourceTrigger);
                Assert.Equal(
                    "CanRename",
                    BindingOperations.GetBinding(deviceNameEditor, UIElement.IsEnabledProperty)?.Path.Path);
                Assert.IsType<WrapPanel>(window.FindName("DeviceMetadata"));
                Assert.IsType<TextBlock>(window.FindName("DeviceMacAddress"));
                var pause = Assert.IsType<CheckBox>(window.FindName("PauseIpv4Toggle"));
                Assert.Equal("Pause IPv4", pause.Content);
                var xaml = File.ReadAllText(Path.Combine(
                    GetProjectRoot(),
                    "src",
                    "Lantern.App",
                    "MainWindow.xaml"));
                Assert.DoesNotContain("Text=\"LOCAL PROCESSING\"", xaml, StringComparison.Ordinal);
                Assert.DoesNotContain("Text=\"1 sec live\"", xaml, StringComparison.Ordinal);
                Assert.DoesNotContain("1 second samples", xaml, StringComparison.Ordinal);
                Assert.DoesNotContain("Text=\"Visited domains\"", xaml, StringComparison.Ordinal);
                Assert.DoesNotContain("Content=\"Paused\"", xaml, StringComparison.Ordinal);
                Assert.Contains("ItemsSource=\"{Binding DomainPresetRules}\"", xaml, StringComparison.Ordinal);
                Assert.Contains("Text=\"{Binding PresetName}\"", xaml, StringComparison.Ordinal);
                Assert.Contains("ItemsSource=\"{Binding Domains}\"", xaml, StringComparison.Ordinal);
                Assert.Contains("IsExpanded=\"{Binding IsExpanded, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
                chart.Measure(new System.Windows.Size(800, 238));
                chart.Arrange(new System.Windows.Rect(0, 0, 800, 238));
                var emptyChartBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    800,
                    238,
                    96,
                    96,
                    System.Windows.Media.PixelFormats.Pbgra32);
                emptyChartBitmap.Render(chart);
                foreach (var (name, path) in new[]
                {
                        ("DownloadLimitEditor", "DownloadLimit"),
                        ("UploadLimitEditor", "UploadLimit"),
                    })
                {
                    var editor = Assert.IsType<TextBox>(window.FindName(name));
                    var binding = Assert.IsType<Binding>(
                        BindingOperations.GetBinding(editor, TextBox.TextProperty));
                    Assert.Equal(path, binding.Path.Path);
                    Assert.Equal(UpdateSourceTrigger.PropertyChanged, binding.UpdateSourceTrigger);
                    Assert.True(
                        binding.Delay >= 300,
                        "Limit changes must be debounced so typing 1000 does not apply 1, 10, and 100 first.");
                }

                foreach (var (name, readable, exact) in new[]
                {
                        ("UsageDownloadCell", "UsageDownloadText", "UsageDownloadExactText"),
                        ("UsageUploadCell", "UsageUploadText", "UsageUploadExactText"),
                        ("UsageTotalCell", "UsageTotalText", "UsageTotalExactText"),
                    })
                {
                    var cell = Assert.IsType<StackPanel>(window.FindName(name));
                    var bindings = cell.Children
                        .OfType<TextBlock>()
                        .Select(text => BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path)
                        .Where(path => path is not null)
                        .ToArray();
                    Assert.Equal(new[] { readable, exact }, bindings);
                }

                var now = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
                DeviceViewModel CreateDevice(
                    string mac,
                    string ip,
                    string name,
                    double download,
                    bool isGateway)
                {
                    var snapshot = new DeviceSnapshot(
                        PhysicalAddress.Parse(mac),
                        IPAddress.Parse(ip),
                        name,
                        now,
                        now,
                        download,
                        0);
                    var device = new DeviceViewModel(_ => Task.CompletedTask);
                    device.Initialize(
                        snapshot,
                        null,
                        isGateway,
                        isGateway ? "Gateway — protected" : "Online");
                    return device;
                }

                var phoneB = CreateDevice(
                    "0E4F69CCE4F0",
                    "192.168.31.213",
                    "Phone B",
                    10,
                    false);
                var gateway = CreateDevice(
                    "64644A380A15",
                    "192.168.31.1",
                    "Gateway",
                    10_000,
                    true);
                var phoneA = CreateDevice(
                    "D2574CDCA5B2",
                    "192.168.31.225",
                    "Phone A",
                    1_000,
                    false);
                window.Devices.Add(phoneB);
                window.Devices.Add(gateway);
                window.Devices.Add(phoneA);

                var deviceView = CollectionViewSource.GetDefaultView(window.Devices);
                deviceView.Refresh();
                Assert.Equal(
                    new[] { "Phone A", "Phone B", "Gateway" },
                    deviceView.Cast<DeviceViewModel>().Select(device => device.DisplayName));

                phoneB.Update(
                    new DeviceSnapshot(
                        PhysicalAddress.Parse("0E4F69CCE4F0"),
                        IPAddress.Parse("192.168.31.213"),
                        "Phone B",
                        now,
                        now,
                        50_000,
                        0),
                    null);
                deviceView.Refresh();
                Assert.Equal(
                    new[] { "Phone A", "Phone B", "Gateway" },
                    deviceView.Cast<DeviceViewModel>().Select(device => device.DisplayName));

                var topStatus = Assert.IsType<TextBlock>(window.FindName("TopStatusText"));
                var uptime = Assert.IsType<TextBlock>(window.FindName("UptimeText"));
                topStatus.Text = "Active";
                uptime.Text = "Active 00:36:10";
                var refreshTimer = Assert.IsType<System.Windows.Threading.DispatcherTimer>(
                    typeof(MainWindow)
                        .GetField(
                            "refreshTimer",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic)!
                        .GetValue(window));
                refreshTimer.Start();
                var applyEngineState = typeof(MainWindow).GetMethod(
                    "ApplyEngineState",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
                Assert.NotNull(applyEngineState);
                applyEngineState.Invoke(window, [false]);

                Assert.Equal("Idle", topStatus.Text);
                Assert.Equal("Idle", uptime.Text);
                Assert.False(refreshTimer.IsEnabled);
                Assert.Equal(Visibility.Visible, Assert.IsType<Button>(window.FindName("StartButton")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Button>(window.FindName("StopButton")).Visibility);

                window.Close();
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Initialize_RestoresBothSavedBandwidthLimits()
    {
        var now = DateTimeOffset.Parse("2026-07-31T00:00:00Z");
        var snapshot = new DeviceSnapshot(
            PhysicalAddress.Parse("E261190DBD54"),
            IPAddress.Parse("192.168.31.61"),
            "Phone",
            now,
            now,
            0,
            0);
        var preferences = new DevicePreferences
        {
            DownloadKiloBytesPerSecond = 250,
            UploadKiloBytesPerSecond = 75,
        };
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);

        viewModel.Initialize(snapshot, preferences, false, "Online");

        Assert.Equal(250, viewModel.DownloadLimit);
        Assert.Equal(75, viewModel.UploadLimit);
    }

    [Fact]
    public void Update_ExposesNumericRatesForDashboardAggregation()
    {
        var now = DateTimeOffset.Parse("2026-07-31T00:00:00Z");
        var snapshot = new DeviceSnapshot(
            PhysicalAddress.Parse("0E4F69CCE4F0"),
            IPAddress.Parse("192.168.31.213"),
            "POCO-F6",
            now,
            now,
            1_250,
            375);
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);

        viewModel.Initialize(snapshot, null, false, "Online");

        Assert.Equal(1_250, viewModel.DownloadBytesPerSecond);
        Assert.Equal(375, viewModel.UploadBytesPerSecond);
    }

    [Fact]
    public void Update_ShowsCumulativeUsageInBytesAndReadableUnits()
    {
        var now = DateTimeOffset.Parse("2026-11-15T00:00:00Z");
        var snapshot = new DeviceSnapshot(
            PhysicalAddress.Parse("0E4F69CCE4F0"),
            IPAddress.Parse("192.168.31.213"),
            "POCO-F6",
            now,
            now,
            1_250,
            375,
            UsageDownloadBytes: 3_456_789_012,
            UsageUploadBytes: 123_456_789);
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);

        viewModel.Initialize(snapshot, null, false, "Online");

        Assert.Equal(1_250, viewModel.DownloadBytesPerSecond);
        Assert.Equal(3_456_789_012, viewModel.UsageDownloadBytes);
        Assert.Equal(123_456_789, viewModel.UsageUploadBytes);
        Assert.Equal(3_580_245_801, viewModel.UsageTotalBytes);
        Assert.Equal("3.46 GB", viewModel.UsageDownloadText);
        Assert.Equal("123.5 MB", viewModel.UsageUploadText);
        Assert.Equal("3.58 GB", viewModel.UsageTotalText);
        Assert.Equal("3,580,245,801 bytes", viewModel.UsageTotalExactText);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1_500, "1.5 KB")]
    [InlineData(25_300_000, "25.3 MB")]
    [InlineData(7_250_000_000, "7.25 GB")]
    [InlineData(1_200_000_000_000, "1.20 TB")]
    public void FormatBytes_UsesReadableUnits(long bytes, string expected)
    {
        Assert.Equal(expected, DeviceViewModel.FormatBytes(bytes));
    }

    [Fact]
    public void EditableName_SavesManualAliasAndOverridesAutomaticNames()
    {
        var now = DateTimeOffset.Parse("2026-07-31T00:00:00Z");
        var snapshot = new DeviceSnapshot(
            PhysicalAddress.Parse("0E4F69CCE4F0"),
            IPAddress.Parse("192.168.31.213"),
            "POCO-F6",
            now,
            now,
            0,
            0);
        var changes = 0;
        var viewModel = new DeviceViewModel(
            _ =>
            {
                changes++;
                return Task.CompletedTask;
            });
        viewModel.Initialize(snapshot, null, false, "Online");

        viewModel.EditableName = "  Omar's phone  ";
        viewModel.Update(snapshot with { HostName = "Android" }, null);

        Assert.Equal("Omar's phone", viewModel.Alias);
        Assert.Equal("Omar's phone", viewModel.DisplayName);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SetPresence_ShowsObservationRecencyWithoutClaimingConnectivity()
    {
        var now = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var snapshot = new DeviceSnapshot(
            PhysicalAddress.Parse("0E4F69CCE4F0"),
            IPAddress.Parse("192.168.31.213"),
            "POCO-F6",
            now,
            now,
            0,
            0);
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);
        viewModel.Initialize(snapshot, null, false, "Online");

        viewModel.SetPresence(false);

        Assert.False(viewModel.IsOnline);
        Assert.Equal("Not recently seen", viewModel.PresenceText);
        Assert.Empty(viewModel.ProtectedReason);
        Assert.Equal(now, viewModel.ObservedAt);
    }

    [Fact]
    public void HasActiveRule_IsFalseForProtectedDevicesEvenWithSavedLimits()
    {
        var now = DateTimeOffset.Parse("2026-07-31T00:00:00Z");
        var snapshot = new DeviceSnapshot(
            PhysicalAddress.Parse("64644A380A15"),
            IPAddress.Parse("192.168.31.1"),
            "Gateway",
            now,
            now,
            0,
            0);
        var preferences = new DevicePreferences
        {
            DownloadKiloBytesPerSecond = 500,
        };
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);

        viewModel.Initialize(snapshot, preferences, true, "Gateway — protected");

        Assert.False(viewModel.HasActiveRule);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetMonitoring_StoppedRatesAreUnavailableWithoutChangingObservedPresence(bool recentlySeen)
    {
        var snapshot = CreateSnapshot();
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);
        viewModel.Initialize(snapshot, null, false, "Online");
        viewModel.SetPresence(recentlySeen);

        viewModel.SetMonitoring(false);
        viewModel.Update(snapshot with { DownloadBytesPerSecond = 9_000, UploadBytesPerSecond = 4_000 }, null);

        Assert.False(viewModel.IsMonitoring);
        Assert.Equal("—", viewModel.DownloadRate);
        Assert.Equal("—", viewModel.UploadRate);
        Assert.Equal(0, viewModel.DownloadBytesPerSecond);
        Assert.Equal(0, viewModel.UploadBytesPerSecond);
        Assert.Equal(0, viewModel.TotalRate);
        Assert.Equal(recentlySeen, viewModel.IsOnline);
        Assert.Equal(snapshot.LastSeen, viewModel.ObservedAt);
        Assert.Equal(snapshot.UsageTotalBytes, viewModel.UsageTotalBytes);
        Assert.Contains("rates unavailable", viewModel.MonitoringStateText, StringComparison.Ordinal);
    }

    [Fact]
    public void SetMonitoring_RestartDoesNotResurrectAnOldRate()
    {
        var snapshot = CreateSnapshot();
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);
        viewModel.Initialize(snapshot, null, false, "Online");
        viewModel.SetMonitoring(false);

        viewModel.SetMonitoring(true);

        Assert.Equal(0, viewModel.TotalRate);
        Assert.Equal("0 B/s", viewModel.DownloadRate);
        Assert.Equal("0 B/s", viewModel.UploadRate);

        viewModel.Update(snapshot with { DownloadBytesPerSecond = 600, UploadBytesPerSecond = 90 }, null);

        Assert.Equal(600, viewModel.DownloadBytesPerSecond);
        Assert.Equal(90, viewModel.UploadBytesPerSecond);
        Assert.Equal(690, viewModel.TotalRate);
    }

    [Fact]
    public void ConfiguredPolicy_RemainsSavedWhenControlStopsOrPresenceExpires()
    {
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);
        viewModel.Initialize(CreateSnapshot(), null, false, "Online");
        viewModel.DownloadLimit = 250;
        viewModel.PauseInternet = true;

        viewModel.SetMonitoring(false);
        viewModel.SetPresence(false);

        Assert.True(viewModel.HasConfiguredRule);
        Assert.False(viewModel.HasActiveRule);
        Assert.Equal("IPv4 pause saved", viewModel.PolicyStateText);
        Assert.Contains("control stopped", viewModel.PauseStateText, StringComparison.Ordinal);
        Assert.Equal("Monitoring stopped", viewModel.PresenceText);
        Assert.Empty(viewModel.ProtectedReason);
    }

    [Fact]
    public void Initialize_ProtectedLocalComputerCanBeRenamedButNotControlled()
    {
        var changes = 0;
        var viewModel = new DeviceViewModel(
            _ =>
            {
                changes++;
                return Task.CompletedTask;
            });
        viewModel.Initialize(
            CreateSnapshot("Desktop"),
            new DevicePreferences { DownloadKiloBytesPerSecond = 500, PauseInternet = true },
            true,
            "This computer — protected");

        viewModel.EditableName = "  My workstation  ";
        viewModel.DownloadLimit = 100;
        viewModel.PauseInternet = true;
        viewModel.SetPresence(false);

        Assert.True(viewModel.IsLocalComputer);
        Assert.False(viewModel.IsGateway);
        Assert.True(viewModel.CanRename);
        Assert.False(viewModel.CanControl);
        Assert.False(viewModel.HasConfiguredRule);
        Assert.False(viewModel.PauseInternet);
        Assert.Equal(500, viewModel.DownloadLimit);
        Assert.Equal("My workstation", viewModel.DisplayName);
        Assert.Equal("My workstation", viewModel.Alias);
        Assert.Equal("This computer — protected", viewModel.ProtectedReason);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Initialize_ProtectedGatewayCannotBeRenamed()
    {
        var changes = 0;
        var viewModel = new DeviceViewModel(
            _ =>
            {
                changes++;
                return Task.CompletedTask;
            });
        viewModel.Initialize(CreateSnapshot("Gateway"), null, true, "Gateway — protected");

        viewModel.EditableName = "Another name";

        Assert.True(viewModel.IsGateway);
        Assert.False(viewModel.IsLocalComputer);
        Assert.False(viewModel.CanRename);
        Assert.Null(viewModel.Alias);
        Assert.Equal("Gateway", viewModel.DisplayName);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Initialize_UnknownIdentityDoesNotPutAnAddressInTheDeviceName()
    {
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);

        viewModel.Initialize(CreateSnapshot(null, IPAddress.Any), null, false, "Online");

        Assert.Equal("Unknown device", viewModel.DisplayName);
        Assert.Equal("Unknown device", viewModel.AutomaticName);
        Assert.Equal("-", viewModel.IpAddress);
        Assert.Empty(viewModel.EditableAlias);
    }

    [Fact]
    public void LastSeenText_UsesTheHistoricalSnapshotTimeAndHandlesUnknownTime()
    {
        var snapshot = CreateSnapshot();
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);
        viewModel.Initialize(snapshot, null, false, "Online");

        Assert.Equal(snapshot.LastSeen, viewModel.ObservedAt);
        Assert.Equal(
            $"Last observed {snapshot.LastSeen.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            viewModel.LastSeenText);

        viewModel.Update(snapshot with { LastSeen = default }, null);

        Assert.Equal("-", viewModel.LastSeenText);
    }

    [Fact]
    public void EditableName_ClearsToTheActualAutomaticNameWithoutRestoringAStaleAlias()
    {
        var snapshot = CreateSnapshot("Automatic hostname");
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);
        viewModel.Initialize(snapshot, new DevicePreferences { Alias = "My device" }, false, "Online");
        Assert.Equal("My device", viewModel.EditableName);

        viewModel.EditableName = " ";
        viewModel.Update(snapshot with { HostName = "New automatic hostname" }, "My device");

        Assert.Null(viewModel.Alias);
        Assert.Empty(viewModel.EditableAlias);
        Assert.Equal("New automatic hostname", viewModel.DisplayName);
        Assert.Equal("New automatic hostname", viewModel.AutomaticName);
    }

    [Fact]
    public void Update_DoesNotRefreshTheAliasEditorDuringAutomaticNameChanges()
    {
        var snapshot = CreateSnapshot("Initial hostname");
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);
        viewModel.Initialize(snapshot, null, false, "Online");
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        viewModel.Update(snapshot with { HostName = "Updated hostname" }, "An out-of-date alias");

        Assert.Equal("Updated hostname", viewModel.DisplayName);
        Assert.Empty(viewModel.EditableAlias);
        Assert.DoesNotContain(nameof(DeviceViewModel.EditableAlias), notifications);
        Assert.DoesNotContain(nameof(DeviceViewModel.Alias), notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetProtection_ReclassifiesAdapterRolesWithoutLosingAliasOrSavedPolicy(bool initiallyProtected)
    {
        var changes = 0;
        var viewModel = new DeviceViewModel(
            _ =>
            {
                changes++;
                return Task.CompletedTask;
            });
        viewModel.Initialize(
            CreateSnapshot(),
            new DevicePreferences { Alias = "My device", PauseInternet = true },
            initiallyProtected,
            initiallyProtected ? "This computer — protected" : "Online");

        viewModel.SetProtection(true, "This computer — protected");

        Assert.True(viewModel.IsLocalComputer);
        Assert.True(viewModel.CanRename);
        Assert.False(viewModel.CanControl);
        Assert.False(viewModel.HasConfiguredRule);
        Assert.False(viewModel.HasActiveRule);
        Assert.Equal("My device", viewModel.Alias);
        Assert.Equal("My device", viewModel.EditableName);

        viewModel.SetProtection(true, "Gateway — protected");

        Assert.True(viewModel.IsGateway);
        Assert.False(viewModel.IsLocalComputer);
        Assert.False(viewModel.CanRename);
        Assert.Equal("My device", viewModel.Alias);

        viewModel.SetProtection(false, "Online");

        Assert.True(viewModel.CanControl);
        Assert.True(viewModel.CanRename);
        Assert.False(viewModel.IsGateway);
        Assert.True(viewModel.HasConfiguredRule);
        Assert.True(viewModel.PauseInternet);
        Assert.Empty(viewModel.ProtectedReason);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void SetMonitoring_NotifiesPresenceLabelWithoutChangingLastObservedTime()
    {
        var snapshot = CreateSnapshot();
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);
        viewModel.Initialize(snapshot, null, false, "Online");
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        viewModel.SetMonitoring(false);

        Assert.Equal("Monitoring stopped", viewModel.PresenceText);
        Assert.Equal(snapshot.LastSeen, viewModel.ObservedAt);
        Assert.Contains(nameof(DeviceViewModel.PresenceText), notifications);

        viewModel.SetMonitoring(true);

        Assert.Equal("Recently seen", viewModel.PresenceText);
    }

    [Fact]
    public void SetCaptureCoverage_NotifiesOnlyCoverageWithoutSavingOrChangingDevicePolicy()
    {
        var changes = 0;
        var viewModel = new DeviceViewModel(
            _ =>
            {
                changes++;
                return Task.CompletedTask;
            });
        viewModel.Initialize(
            CreateSnapshot(),
            new DevicePreferences { Alias = "My device", DownloadKiloBytesPerSecond = 250, PauseInternet = true },
            false,
            "Online");
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        const string text = "Last capture session: ↑12 packets (1.2 KB) ↓0 packets (0 B). Upload only observed.";

        viewModel.SetCaptureCoverage(text);
        viewModel.SetCaptureCoverage(text);

        Assert.Equal(text, viewModel.CaptureCoverageText);
        Assert.Equal(new[] { nameof(DeviceViewModel.CaptureCoverageText) }, notifications);
        Assert.Equal(0, changes);
        Assert.Equal("My device", viewModel.Alias);
        Assert.Equal(250, viewModel.DownloadLimit);
        Assert.True(viewModel.PauseInternet);
        Assert.True(viewModel.HasConfiguredRule);
        Assert.Equal(1_250, viewModel.DownloadBytesPerSecond);
        Assert.Equal(375, viewModel.UploadBytesPerSecond);
    }

    [Fact]
    public void SetCaptureCoverage_LastSessionTextSurvivesStoppedMonitoringAndSnapshotRefresh()
    {
        var snapshot = CreateSnapshot();
        var viewModel = new DeviceViewModel(_ => Task.CompletedTask);
        viewModel.Initialize(snapshot, null, false, "Online");
        const string text = "Last capture session: ↑12 packets (1.2 KB) ↓8 packets (6.0 KB). Both directions observed.";
        viewModel.SetCaptureCoverage(text);

        viewModel.SetMonitoring(false);
        viewModel.Update(snapshot, null);

        Assert.Equal(text, viewModel.CaptureCoverageText);
        Assert.Equal("—", viewModel.DownloadRate);
        Assert.Equal("—", viewModel.UploadRate);
    }

    private static DeviceSnapshot CreateSnapshot(string? hostName = "Phone", IPAddress? address = null)
    {
        var now = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        return new DeviceSnapshot(
            PhysicalAddress.Parse("0E4F69CCE4F0"),
            address ?? IPAddress.Parse("192.168.31.213"),
            hostName,
            now.AddMinutes(-5),
            now,
            1_250,
            375,
            UsageDownloadBytes: 5_000,
            UsageUploadBytes: 800);
    }

    private static string GetProjectRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));

    private static double ContrastRatio(
        System.Windows.Media.Color lighter,
        System.Windows.Media.Color darker)
    {
        var first = RelativeLuminance(lighter);
        var second = RelativeLuminance(darker);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static double RelativeLuminance(System.Windows.Media.Color color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255D;
            return normalized <= 0.04045
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) +
               (0.7152 * Channel(color.G)) +
               (0.0722 * Channel(color.B));
    }
}
