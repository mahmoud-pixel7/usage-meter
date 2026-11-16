using System.Net.NetworkInformation;
using Lantern.App.Services;
using Lantern.Core.Control;
using Lantern.Core.Networking;
using Xunit;

namespace Lantern.App.Tests;

public sealed class CaptureDiagnosticsTests
{
    private static readonly PhysicalAddress ClientMac = PhysicalAddress.Parse("E261190DBD54");
    private static readonly PhysicalAddress LocalMac = PhysicalAddress.Parse("345A6063C052");

    [Fact]
    public void CapturedProtocolsAndIgnoreReasons_DoNotBecomeUsageBytes()
    {
        var diagnostics = new CaptureDiagnostics();
        diagnostics.RecordCapturedFrame(EthernetFrame(0x0800));
        diagnostics.RecordCapturedFrame(EthernetFrame(0x86dd));
        diagnostics.RecordCapturedFrame(EthernetFrame(0x0806));
        diagnostics.RecordCapturedFrame([]);
        diagnostics.RecordArpFrame();
        diagnostics.RecordRoute(new FrameRouteResult(
            FrameAction.Ignore, IgnoreReason: FrameIgnoreReason.Ipv6ObservationOnly));
        diagnostics.RecordRoute(new FrameRouteResult(
            FrameAction.Ignore, IgnoreReason: FrameIgnoreReason.UnknownClient));

        var snapshot = diagnostics.Snapshot();

        Assert.Equal(1, snapshot.CapturedIpv4Frames);
        Assert.Equal(1, snapshot.CapturedIpv6Frames);
        Assert.Equal(1, snapshot.CapturedOtherFrames);
        Assert.Equal(1, snapshot.CapturedMalformedFrames);
        Assert.Equal(1, snapshot.CapturedArpFrames);
        Assert.Equal(1, snapshot.IgnoredFrames[FrameIgnoreReason.Ipv6ObservationOnly]);
        Assert.Equal(1, snapshot.IgnoredFrames[FrameIgnoreReason.UnknownClient]);
        Assert.Equal(0, snapshot.RoutedDownloadPayloadBytes + snapshot.RoutedUploadPayloadBytes);
        Assert.Equal(0, snapshot.LocalDownloadPayloadBytes + snapshot.LocalUploadPayloadBytes);
    }

    [Fact]
    public void PayloadCounters_UseOnlyLegacyMeteredBytesAndKeepTheLocalPathSeparate()
    {
        var diagnostics = new CaptureDiagnostics();
        diagnostics.RecordRoute(new FrameRouteResult(
            FrameAction.Forward, TrafficDirection.Download, ClientMac,
            new byte[154], MeteredByteCount: 100));
        diagnostics.RecordRoute(new FrameRouteResult(
            FrameAction.Forward, TrafficDirection.Upload, ClientMac,
            new byte[104], MeteredByteCount: 50));
        diagnostics.RecordRoute(new FrameRouteResult(
            FrameAction.Drop, TrafficDirection.Upload, ClientMac));
        diagnostics.RecordLocalTraffic(LocalMac, new LocalTrafficMeasurement(TrafficDirection.Download, 200));
        diagnostics.RecordLocalTraffic(LocalMac, new LocalTrafficMeasurement(TrafficDirection.Upload, 300));
        diagnostics.RecordForwardedPacket();
        diagnostics.RecordInjectionFailure(arp: false);

        var snapshot = diagnostics.Snapshot();
        var coverage = diagnostics.GetCoverage(ClientMac);

        Assert.Equal(100, snapshot.RoutedDownloadPayloadBytes);
        Assert.Equal(50, snapshot.RoutedUploadPayloadBytes);
        Assert.Equal(200, snapshot.LocalDownloadPayloadBytes);
        Assert.Equal(300, snapshot.LocalUploadPayloadBytes);
        Assert.Equal(1, snapshot.ForwardedPackets);
        Assert.Equal(1, snapshot.DroppedPackets);
        Assert.Equal(1, snapshot.ForwardingInjectionFailures);
        Assert.Equal(3, coverage.ObservedPackets);
        Assert.Equal(1, coverage.DroppedPackets);
        Assert.Equal(100, coverage.DownloadPayloadBytes);
        Assert.Equal(50, coverage.UploadPayloadBytes);
        Assert.False(coverage.IsLocalComputer);
        Assert.True(diagnostics.GetCoverage(LocalMac).IsLocalComputer);
    }

    [Fact]
    public void Coverage_ZeroObservedIsACautionNotAClaimOfZeroUsageOrCompleteDelivery()
    {
        var diagnostics = new CaptureDiagnostics();

        Assert.Contains("not proof of zero", diagnostics.GetCoverage(ClientMac).Status);
        diagnostics.RecordRoute(new FrameRouteResult(
            FrameAction.Forward, TrafficDirection.Download, ClientMac, MeteredByteCount: 100));
        Assert.Contains("does not prove complete coverage or delivery", diagnostics.GetCoverage(ClientMac).Status);
        diagnostics.RecordLocalTraffic(LocalMac, new LocalTrafficMeasurement(TrafficDirection.Upload, 100));
        Assert.Contains("IPv6 and private/local peers are excluded", diagnostics.GetCoverage(LocalMac).Status);
    }

    [Fact]
    public void FailuresAmbiguityAndUnavailableDriverStatistics_AreShownWithoutInventingZeroDrops()
    {
        var diagnostics = new CaptureDiagnostics();
        diagnostics.RecordInjectionFailure(arp: true);
        diagnostics.RecordInjectionFailure(arp: false);
        diagnostics.RecordCaptureReadFailure();
        diagnostics.RecordMappingUpdate(new ClientMappingUpdate(true, true, true, 1));
        diagnostics.RecordMappingUpdate(new ClientMappingUpdate(
            false, false, false, 0, ClientMappingRejection.GatewayOrProxyMac));
        diagnostics.SetCaptureStatistics(false, new CaptureHandleStatistics(100, 5, 2));
        diagnostics.SetCaptureStatistics(true, null, "driver does not support statistics");

        var snapshot = diagnostics.Snapshot();
        var summary = CaptureDiagnosticsFormatter.Format(snapshot);

        Assert.Equal(1, snapshot.ArpInjectionFailures);
        Assert.Equal(1, snapshot.ForwardingInjectionFailures);
        Assert.Equal(1, snapshot.CaptureReadFailures);
        Assert.Equal(1, snapshot.MappingAmbiguities);
        Assert.Equal(1, snapshot.RejectedMappings);
        Assert.Equal(1, snapshot.RejectedMappingReasons[ClientMappingRejection.GatewayOrProxyMac]);
        Assert.Contains("not complete router/billing usage", summary);
        Assert.Contains("IPv6 is observed, never forwarded or counted", summary);
        Assert.Contains("no backfill", summary);
        Assert.Contains("not proof of delivery", summary);
        Assert.Contains("Latest-address-only mapping is ambiguous", summary);
        Assert.Contains("100/5/2", summary);
        Assert.Contains("unavailable (driver does not support statistics)", summary);
        Assert.Contains("not added together", summary);
    }

    [Fact]
    public void FullRestorationJournal_IsExplicitAndNeverClaimedAsRoutingCoverage()
    {
        var diagnostics = new CaptureDiagnostics();
        diagnostics.RecordMappingUpdate(new ClientMappingUpdate(
            false, false, false, 0, ClientMappingRejection.RestorationCapacityReached));
        var snapshot = diagnostics.Snapshot() with { RestorationTargets = 4_096 };

        var summary = CaptureDiagnosticsFormatter.Format(snapshot);

        Assert.Contains("RestorationCapacityReached 1", summary);
        Assert.Contains("restore-only journal 4,096 (not a routing source)", summary);
        Assert.Contains("additional interception is skipped", summary);
        Assert.Equal(0, snapshot.RoutedDownloadPayloadBytes + snapshot.RoutedUploadPayloadBytes);
        Assert.Equal(0, snapshot.LocalDownloadPayloadBytes + snapshot.LocalUploadPayloadBytes);
    }

    [Fact]
    public async Task Counters_AreSafeForConcurrentCaptureAndUiSnapshots()
    {
        var diagnostics = new CaptureDiagnostics();
        var counting = Task.Run(() => Parallel.For(0, 2_000, _ =>
        {
            diagnostics.RecordCapturedFrame(EthernetFrame(0x0800));
            diagnostics.RecordRoute(new FrameRouteResult(
                FrameAction.Forward, TrafficDirection.Upload, ClientMac, MeteredByteCount: 10));
        }));
        var reading = Task.Run(() =>
        {
            for (var index = 0; index < 200; index++)
            {
                diagnostics.Snapshot();
            }
        });
        await Task.WhenAll(counting, reading);

        var snapshot = diagnostics.Snapshot();
        Assert.Equal(2_000, snapshot.CapturedIpv4Frames);
        Assert.Equal(20_000, snapshot.RoutedUploadPayloadBytes);
        Assert.Equal(2_000, diagnostics.GetCoverage(ClientMac).ObservedPackets);
    }

    private static byte[] EthernetFrame(ushort etherType)
    {
        var frame = new byte[64];
        frame[12] = (byte)(etherType >> 8);
        frame[13] = (byte)etherType;
        return frame;
    }
}
