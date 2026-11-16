using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace Lantern.App.Tests;

public sealed class MonitoringUiContractTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void DeviceTable_IsASixColumnOverviewWithEditingAndExactTotalsInSelectedDetails()
    {
        var document = LoadWindow();
        var grid = Named(document, "DeviceGrid");
        var columns = grid.Element(Presentation + "DataGrid.Columns")!.Elements().ToArray();

        Assert.Equal(
            new[] { "DEVICE", "IP / PRESENCE", "DOWN NOW", "UP NOW", "MEASURED", "POLICY" },
            columns.Select(column => (string?)column.Attribute("Header")));
        Assert.Equal("True", (string?)grid.Attribute("IsReadOnly"));
        Assert.Equal("Single", (string?)grid.Attribute("SelectionMode"));
        Assert.DoesNotContain(grid.Descendants(), element => element.Name == Presentation + "TextBox");
        Assert.DoesNotContain(grid.Descendants(), element => element.Name == Presentation + "CheckBox");
        var overview = grid.ToString();
        Assert.DoesNotContain("MacAddress", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("ExactText", overview, StringComparison.Ordinal);
        Assert.Contains("{Binding IpAddress}", overview, StringComparison.Ordinal);
        Assert.Contains("{Binding PresenceText}", overview, StringComparison.Ordinal);

        var details = Named(document, "SelectedDeviceDetails");
        Assert.Equal("{Binding SelectedItem, ElementName=DeviceGrid}", (string?)details.Attribute("DataContext"));
        var detailsMarkup = details.ToString();
        foreach (var binding in new[]
        {
            "MacAddress",
            "UsageDownloadExactText",
            "UsageUploadExactText",
            "UsageTotalExactText",
            "LastSeenText",
            "CaptureCoverageText",
            "EditableAlias",
            "DownloadLimit",
            "UploadLimit",
            "PauseInternet",
        })
        {
            Assert.Contains($"{{Binding {binding}", detailsMarkup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ShellStatusAndMeasurementScope_ArePersistentOutsideScrollableNavigationContent()
    {
        var document = LoadWindow();
        foreach (var name in new[] { "DetailStatusText", "CaptureDiagnosticsText", "CaptureScopeNoticeText", "SavedDataWarningText", "UptimeText" })
        {
            var element = Named(document, name);
            Assert.Contains(element.Ancestors(), ancestor => (string?)ancestor.Attribute(Xaml + "Name") == "MonitoringStatusStrip");
            Assert.DoesNotContain(element.Ancestors(), ancestor => (string?)ancestor.Attribute(Xaml + "Name") == "MainScrollViewer");
            Assert.DoesNotContain(element.Ancestors(), ancestor => (string?)ancestor.Attribute(Xaml + "Name") == "DeviceSection");
            Assert.DoesNotContain(element.Ancestors(), ancestor => (string?)ancestor.Attribute(Xaml + "Name") == "OverviewSection");
        }

        var scope = (string?)Named(document, "CaptureScopeNoticeText").Attribute("Text") ?? string.Empty;
        Assert.Contains("observed IPv4 payload only", scope, StringComparison.Ordinal);
        Assert.Contains("IPv6 internet coverage unavailable", scope, StringComparison.Ordinal);
        Assert.Contains("zero observed != zero used", scope, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureDiagnostics_AreCollapsedByDefaultWithoutHidingStatusScopeOrSavedDataWarnings()
    {
        var document = LoadWindow();
        var expander = Named(document, "CaptureDiagnosticsExpander");
        Assert.Equal("Capture diagnostics", (string?)expander.Attribute("Header"));
        Assert.Equal("False", (string?)expander.Attribute("IsExpanded"));
        Assert.Contains(expander, Named(document, "CaptureDiagnosticsText").Ancestors());

        foreach (var name in new[] { "DetailStatusText", "CaptureScopeNoticeText", "SavedDataWarningText" })
        {
            var element = Named(document, name);
            Assert.Same(expander.Parent, element.Parent);
            Assert.DoesNotContain(element.Ancestors(), ancestor => ancestor.Name == Presentation + "Expander");
            Assert.DoesNotContain(element.Ancestors(), ancestor => (string?)ancestor.Attribute("Visibility") is "Collapsed" or "Hidden");
        }

        var warning = Named(document, "SavedDataWarningText");
        Assert.Equal("Collapsed", (string?)warning.Attribute("Visibility"));
        Assert.Equal("Wrap", (string?)warning.Attribute("TextWrapping"));
        Assert.Equal("2", (string?)warning.Attribute("Grid.Row"));
        Assert.Equal("3", (string?)Named(document, "CaptureScopeNoticeText").Attribute("Grid.Row"));
        var coverage = Named(document, "DeviceCaptureCoverageText");
        Assert.Equal("{Binding CaptureCoverageText}", (string?)coverage.Attribute("Text"));
        Assert.Equal("Wrap", (string?)coverage.Attribute("TextWrapping"));
        Assert.Contains(Named(document, "SelectedDeviceDetails"), coverage.Ancestors());
    }

    [Fact]
    public void DisplayedCopy_DoesNotClaimBrowsingHistoryInternetReachabilityOrCompleteUsage()
    {
        var document = LoadWindow();
        var visibleCopy = string.Join(
            "\n",
            document.Descendants().Attributes()
                .Where(attribute => attribute.Name.LocalName is "Text" or "Header" or "Content")
                .Select(attribute => attribute.Value));

        Assert.DoesNotContain("Visited domains", visibleCopy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Internet working", visibleCopy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Total usage", visibleCopy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HITS", visibleCopy, StringComparison.Ordinal);
        Assert.Contains("Observed domains", visibleCopy, StringComparison.Ordinal);
        Assert.Contains("OBSERVATIONS", visibleCopy, StringComparison.Ordinal);
        Assert.Contains("250 device/domain rows retained globally, not saved", visibleCopy, StringComparison.Ordinal);
        Assert.Contains("not visits or browsing history", visibleCopy, StringComparison.Ordinal);
        Assert.Contains("CONFIGURED POLICIES", visibleCopy, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservedDomains_ShowObservationTimesAndKeepCurrentRuleSeparateFromLastSignalOutcome()
    {
        var document = LoadWindow();
        var grid = Named(document, "ObservedDomainsGrid").ToString();

        Assert.Contains("{Binding ObservationCount}", grid, StringComparison.Ordinal);
        Assert.Contains("{Binding FirstSeenText", grid, StringComparison.Ordinal);
        Assert.Contains("{Binding LastSeenText", grid, StringComparison.Ordinal);
        Assert.Contains("{Binding CurrentRuleText}", grid, StringComparison.Ordinal);
        Assert.Contains("{Binding LastObservedDropText}", grid, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding HitCount}", grid, StringComparison.Ordinal);
    }

    [Fact]
    public void AliasAndPauseControls_UseAnExplicitAliasCommitAndAStableScopedActionLabel()
    {
        var document = LoadWindow();
        var alias = Named(document, "DeviceNameEditor");
        Assert.Equal("{Binding EditableAlias, UpdateSourceTrigger=LostFocus}", (string?)alias.Attribute("Text"));
        Assert.Equal("{Binding CanRename}", (string?)alias.Attribute("IsEnabled"));
        var pause = Named(document, "PauseIpv4Toggle");
        Assert.Equal("Pause IPv4", (string?)pause.Attribute("Content"));
        Assert.Equal("{Binding CanControl}", (string?)pause.Attribute("IsEnabled"));
        var details = Named(document, "SelectedDeviceDetails").ToString();
        Assert.Contains("{Binding PauseStateText}", details, StringComparison.Ordinal);
        Assert.Equal("—", (string?)Named(document, "DownloadMetric").Attribute("Text"));
        Assert.Equal("—", (string?)Named(document, "UploadMetric").Attribute("Text"));
    }

    private static XElement Named(XContainer container, string name) =>
        Assert.Single(container.Descendants(), element => (string?)element.Attribute(Xaml + "Name") == name);

    private static XDocument LoadWindow([CallerFilePath] string sourcePath = "") =>
        XDocument.Load(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourcePath)!, "..", "..", "src", "Lantern.App", "MainWindow.xaml")));
}
