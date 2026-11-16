using System.Collections.Specialized;
using Lantern.App.ViewModels;
using Lantern.Core.Networking;
using Xunit;

namespace Lantern.App.Tests;

public sealed class DeviceActivityViewModelTests
{
    [Fact]
    public void DeviceGroup_OrganizesObservedDomainsUnderOneDevice()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var group = new DeviceActivityGroupViewModel(
            "E261190DBD54",
            "Phone",
            "192.168.31.61");
        group.IsExpanded = true;
        var activity = new DeviceActivityViewModel(
            group.MacKey,
            group.DeviceName,
            group.IpAddress,
            "youtube.com",
            DomainObservationSource.Dns,
            observedAt);

        group.AddDomain(activity);
        group.UpdateIdentity("POCO-F6", "192.168.31.213");

        Assert.True(group.HasDomains);
        Assert.Equal("1 observed domain", group.DomainCountText);
        Assert.Equal(1, group.ObservationCount);
        Assert.Equal("POCO-F6", group.DeviceName);
        Assert.Equal("192.168.31.213", group.IpAddress);
        Assert.True(group.IsExpanded);
        Assert.Same(activity, Assert.Single(group.Domains));

        group.RemoveDomain(activity);

        Assert.False(group.HasDomains);
        Assert.Equal("No observed domains", group.DomainCountText);
        Assert.Equal("No observations yet", group.LastSeenText);
    }

    [Fact]
    public void Observe_AggregatesSignalsAndRefreshesDeviceIdentity()
    {
        var firstSeen = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var activity = CreateActivity(firstSeen);

        activity.Observe(
            "POCO-F6",
            "192.168.31.213",
            DomainObservationSource.Tls,
            firstSeen.AddSeconds(5));

        Assert.Equal("POCO-F6", activity.DeviceName);
        Assert.Equal("192.168.31.213", activity.IpAddress);
        Assert.Equal("DNS + TLS", activity.SourceLabel);
        Assert.Equal(2, activity.ObservationCount);
        Assert.Equal(activity.ObservationCount, activity.HitCount);
        Assert.Equal(firstSeen, activity.FirstSeen);
        Assert.Equal(firstSeen.AddSeconds(5), activity.LastSeen);
    }

    [Fact]
    public void ObserveBatch_MergesCountsSourcesAndTimeRangeWithOneSourceNotification()
    {
        var firstSeen = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var activity = CreateActivity(firstSeen);
        var notifications = new List<string?>();
        activity.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        activity.ObserveBatch(
            "Desktop",
            "192.168.31.213",
            [DomainObservationSource.Http, DomainObservationSource.Tls, DomainObservationSource.Dns],
            firstSeen.AddSeconds(-10),
            firstSeen.AddSeconds(5),
            4,
            lastObservedDrop: true);

        Assert.Equal("Desktop", activity.DeviceName);
        Assert.Equal("192.168.31.213", activity.IpAddress);
        Assert.Equal("DNS + TLS + HTTP", activity.SourceLabel);
        Assert.Equal(5, activity.ObservationCount);
        Assert.Equal(firstSeen.AddSeconds(-10), activity.FirstSeen);
        Assert.Equal(firstSeen.AddSeconds(5), activity.LastSeen);
        Assert.True(activity.LastObservedDrop);
        Assert.Single(notifications, name => name == nameof(DeviceActivityViewModel.SourceLabel));
        Assert.Single(notifications, name => name == nameof(DeviceActivityViewModel.ObservationCount));
    }

    [Fact]
    public void ObserveBatch_ZeroAdditionalCountStillMergesTheFirstBatchMetadata()
    {
        var firstSeen = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var activity = CreateActivity(firstSeen);

        activity.ObserveBatch(
            "Phone",
            "192.168.31.61",
            [DomainObservationSource.Dns, DomainObservationSource.Tls],
            firstSeen.AddSeconds(-2),
            firstSeen.AddSeconds(3),
            0,
            lastObservedDrop: true);

        Assert.Equal(1, activity.ObservationCount);
        Assert.Equal("DNS + TLS", activity.SourceLabel);
        Assert.Equal(firstSeen.AddSeconds(-2), activity.FirstSeen);
        Assert.Equal(firstSeen.AddSeconds(3), activity.LastSeen);
        Assert.True(activity.LastObservedDrop);
    }

    [Fact]
    public void ObserveBatch_OlderBatchDoesNotRewriteLatestDropOutcomeOrLastTimestamp()
    {
        var firstSeen = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var activity = CreateActivity(firstSeen);
        activity.Observe("Phone", "192.168.31.61", DomainObservationSource.Tls, firstSeen.AddSeconds(10), true);

        activity.ObserveBatch(
            "Phone",
            "192.168.31.61",
            [DomainObservationSource.Http],
            firstSeen.AddSeconds(-5),
            firstSeen.AddSeconds(2),
            3,
            lastObservedDrop: false);

        Assert.Equal(5, activity.ObservationCount);
        Assert.Equal(firstSeen.AddSeconds(-5), activity.FirstSeen);
        Assert.Equal(firstSeen.AddSeconds(10), activity.LastSeen);
        Assert.True(activity.LastObservedDrop);
        Assert.Equal("Last signal: dropped", activity.LastObservedDropText);
    }

    [Fact]
    public void SetBlocked_ChangesCurrentRuleWithoutRewritingObservedDropHistory()
    {
        var firstSeen = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var activity = CreateActivity(firstSeen);

        activity.SetBlocked(true);

        Assert.True(activity.IsBlocked);
        Assert.False(activity.CanBlock);
        Assert.Equal("Rule exists", activity.BlockActionText);
        Assert.Equal("Block rule configured", activity.CurrentRuleText);
        Assert.False(activity.LastObservedDrop);
        Assert.Equal("Last signal: not dropped", activity.LastObservedDropText);
        Assert.Equal(1, activity.ObservationCount);
        Assert.Equal(firstSeen, activity.LastSeen);

        activity.Observe("Phone", "192.168.31.61", DomainObservationSource.Dns, firstSeen.AddSeconds(1), true);
        activity.SetBlocked(false);

        Assert.False(activity.IsBlocked);
        Assert.True(activity.CanBlock);
        Assert.Equal("Block domain", activity.BlockActionText);
        Assert.Equal("No block rule", activity.CurrentRuleText);
        Assert.True(activity.LastObservedDrop);
    }

    [Fact]
    public void Observe_DoesNotInferACurrentRuleFromADroppedSignal()
    {
        var firstSeen = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var activity = CreateActivity(firstSeen);

        activity.Observe("Phone", "192.168.31.61", DomainObservationSource.Tls, firstSeen.AddSeconds(1), true);

        Assert.True(activity.LastObservedDrop);
        Assert.False(activity.IsBlocked);
        Assert.Equal("No block rule", activity.CurrentRuleText);
    }

    [Fact]
    public void SetControlAvailable_DisablesProtectedActionsWithoutChangingRuleOrObservationHistory()
    {
        var firstSeen = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var activity = CreateActivity(firstSeen);
        var notifications = new List<string?>();
        activity.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        activity.SetControlAvailable(false);

        Assert.False(activity.IsControlAvailable);
        Assert.False(activity.CanBlock);
        Assert.Equal("Protected", activity.BlockActionText);
        Assert.False(activity.IsBlocked);
        Assert.False(activity.LastObservedDrop);
        Assert.Equal(firstSeen, activity.LastSeen);
        Assert.Contains(nameof(DeviceActivityViewModel.CanBlock), notifications);
        Assert.Contains(nameof(DeviceActivityViewModel.BlockActionText), notifications);

        activity.SetControlAvailable(true);

        Assert.True(activity.CanBlock);
        Assert.Equal("Block domain", activity.BlockActionText);
        activity.SetBlocked(true);
        activity.SetControlAvailable(false);
        activity.SetControlAvailable(true);
        Assert.False(activity.CanBlock);
        Assert.Equal("Rule exists", activity.BlockActionText);
    }

    [Fact]
    public void DeviceGroup_TouchingAnExistingDomainDoesNotReorderRowsOrCollapseTheGroup()
    {
        var firstSeen = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var first = CreateActivity(firstSeen, "first.example");
        var second = CreateActivity(firstSeen.AddSeconds(1), "second.example");
        var group = new DeviceActivityGroupViewModel(first.MacKey, first.DeviceName, first.IpAddress)
        {
            IsExpanded = true,
        };
        group.AddDomain(first);
        group.AddDomain(second);
        var collectionChanges = new List<NotifyCollectionChangedAction>();
        group.Domains.CollectionChanged += (_, args) => collectionChanges.Add(args.Action);

        first.ObserveBatch("Phone", "192.168.31.61", [DomainObservationSource.Tls], firstSeen, firstSeen.AddMinutes(1), 5);
        group.TouchDomain(first);
        group.AddDomain(first);

        Assert.Equal(new[] { first, second }, group.Domains);
        Assert.Empty(collectionChanges);
        Assert.True(group.IsExpanded);
        Assert.Equal(7, group.ObservationCount);
        Assert.Equal($"Last observed {first.LastSeen.ToLocalTime():HH:mm:ss}", group.LastSeenText);
    }

    [Fact]
    public void ObserveBatch_RejectsNegativeCountsWithoutMutatingTheRow()
    {
        var firstSeen = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var activity = CreateActivity(firstSeen);

        Assert.Throws<ArgumentOutOfRangeException>(() => activity.ObserveBatch(
            "Other device",
            "192.168.31.99",
            [DomainObservationSource.Tls],
            firstSeen,
            firstSeen.AddSeconds(1),
            -1));

        Assert.Equal("Phone", activity.DeviceName);
        Assert.Equal(1, activity.ObservationCount);
    }

    private static DeviceActivityViewModel CreateActivity(DateTimeOffset observedAt, string domain = "youtube.com") =>
        new(
            "E261190DBD54",
            "Phone",
            "192.168.31.61",
            domain,
            DomainObservationSource.Dns,
            observedAt);
}
