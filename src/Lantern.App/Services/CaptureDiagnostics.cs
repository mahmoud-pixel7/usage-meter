using System.Collections.Concurrent;
using System.Globalization;
using System.Net.NetworkInformation;
using Lantern.Core.Control;
using Lantern.Core.Networking;

namespace Lantern.App.Services;

public sealed record CaptureHandleStatistics(
    long ReceivedPackets,
    long DroppedPackets,
    long InterfaceDroppedPackets);

public sealed record DeviceCaptureCoverage(
    long UploadPackets = 0,
    long DownloadPackets = 0,
    long DroppedPackets = 0,
    long UploadPayloadBytes = 0,
    long DownloadPayloadBytes = 0,
    bool IsLocalComputer = false)
{
    public long ObservedPackets => UploadPackets + DownloadPackets;

    public string Status => ObservedPackets == 0
        ? "No IPv4 traffic observed this session; this is not proof of zero usage."
        : IsLocalComputer
            ? "Original local-PC IPv4 payload observed; IPv6 and private/local peers are excluded."
            : "Routed IPv4 traffic observed; this does not prove complete coverage or delivery.";
}

public sealed record CaptureDiagnosticsSnapshot
{
    public long CapturedIpv4Frames { get; init; }
    public long CapturedIpv6Frames { get; init; }
    public long CapturedOtherFrames { get; init; }
    public long CapturedMalformedFrames { get; init; }
    public long CapturedArpFrames { get; init; }
    public long RoutedUploadPayloadBytes { get; init; }
    public long RoutedDownloadPayloadBytes { get; init; }
    public long LocalUploadPayloadBytes { get; init; }
    public long LocalDownloadPayloadBytes { get; init; }
    public long ForwardedPackets { get; init; }
    public long DroppedPackets { get; init; }
    public long ArpInjectionFailures { get; init; }
    public long ForwardingInjectionFailures { get; init; }
    public long CaptureReadFailures { get; init; }
    public long MappingAmbiguities { get; init; }
    public long RejectedMappings { get; init; }
    public long RestorationTargets { get; init; }
    public IReadOnlyDictionary<FrameIgnoreReason, long> IgnoredFrames { get; init; } =
        new Dictionary<FrameIgnoreReason, long>();
    public IReadOnlyDictionary<ClientMappingRejection, long> RejectedMappingReasons { get; init; } =
        new Dictionary<ClientMappingRejection, long>();
    public CaptureHandleStatistics? ForwardingStatistics { get; init; }
    public CaptureHandleStatistics? ArpStatistics { get; init; }
    public string? ForwardingStatisticsError { get; init; }
    public string? ArpStatisticsError { get; init; }
}

public sealed class CaptureDiagnostics
{
    private readonly ConcurrentDictionary<FrameIgnoreReason, long> ignoredFrames = new();
    private readonly ConcurrentDictionary<ClientMappingRejection, long> rejectedMappingReasons = new();
    private readonly ConcurrentDictionary<string, CoverageCounters> deviceCoverage =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object statisticsSync = new();
    private long capturedIpv4Frames;
    private long capturedIpv6Frames;
    private long capturedOtherFrames;
    private long capturedMalformedFrames;
    private long capturedArpFrames;
    private long routedUploadPayloadBytes;
    private long routedDownloadPayloadBytes;
    private long localUploadPayloadBytes;
    private long localDownloadPayloadBytes;
    private long forwardedPackets;
    private long droppedPackets;
    private long arpInjectionFailures;
    private long forwardingInjectionFailures;
    private long captureReadFailures;
    private long mappingAmbiguities;
    private long rejectedMappings;
    private CaptureHandleStatistics? forwardingStatistics;
    private CaptureHandleStatistics? arpStatistics;
    private string? forwardingStatisticsError;
    private string? arpStatisticsError;

    public void RecordCapturedFrame(ReadOnlySpan<byte> frame)
    {
        switch (TrafficMetering.ClassifyProtocol(frame))
        {
            case CapturedNetworkProtocol.Ipv4:
                Interlocked.Increment(ref capturedIpv4Frames);
                break;
            case CapturedNetworkProtocol.Ipv6:
                Interlocked.Increment(ref capturedIpv6Frames);
                break;
            case CapturedNetworkProtocol.Other:
                Interlocked.Increment(ref capturedOtherFrames);
                break;
            default:
                Interlocked.Increment(ref capturedMalformedFrames);
                break;
        }
    }

    public void RecordArpFrame() => Interlocked.Increment(ref capturedArpFrames);

    public void RecordRoute(FrameRouteResult result)
    {
        if (result.Action == FrameAction.Ignore)
        {
            ignoredFrames.AddOrUpdate(result.IgnoreReason, 1, (_, value) => value + 1);
            return;
        }

        if (result.Action == FrameAction.Drop)
        {
            Interlocked.Increment(ref droppedPackets);
        }

        if (result.Direction is not { } direction || result.ClientMac is not { } mac)
        {
            return;
        }

        var payload = Math.Max(0, result.MeteredByteCount);
        if (direction == TrafficDirection.Upload)
        {
            Interlocked.Add(ref routedUploadPayloadBytes, payload);
        }
        else
        {
            Interlocked.Add(ref routedDownloadPayloadBytes, payload);
        }

        RecordDeviceTraffic(mac, direction, payload, result.Action == FrameAction.Drop, false);
    }

    public void RecordLocalTraffic(PhysicalAddress mac, LocalTrafficMeasurement measurement)
    {
        var payload = Math.Max(0, measurement.PayloadByteCount);
        if (measurement.Direction == TrafficDirection.Upload)
        {
            Interlocked.Add(ref localUploadPayloadBytes, payload);
        }
        else
        {
            Interlocked.Add(ref localDownloadPayloadBytes, payload);
        }

        RecordDeviceTraffic(mac, measurement.Direction, payload, false, true);
    }

    private void RecordDeviceTraffic(
        PhysicalAddress mac,
        TrafficDirection direction,
        int payload,
        bool dropped,
        bool local)
    {
        deviceCoverage.GetOrAdd(mac.ToString(), static _ => new CoverageCounters())
            .Record(direction, payload, dropped, local);
    }

    public void RecordForwardedPacket() => Interlocked.Increment(ref forwardedPackets);

    public void RecordInjectionFailure(bool arp)
    {
        if (arp)
        {
            Interlocked.Increment(ref arpInjectionFailures);
        }
        else
        {
            Interlocked.Increment(ref forwardingInjectionFailures);
        }
    }

    public void RecordCaptureReadFailure() => Interlocked.Increment(ref captureReadFailures);

    public void RecordMappingUpdate(ClientMappingUpdate update)
    {
        if (update.IsAmbiguous)
        {
            Interlocked.Increment(ref mappingAmbiguities);
        }

        if (!update.Accepted)
        {
            Interlocked.Increment(ref rejectedMappings);
            rejectedMappingReasons.AddOrUpdate(update.Rejection, 1, (_, value) => value + 1);
        }
    }

    public void SetCaptureStatistics(
        bool arp,
        CaptureHandleStatistics? statistics,
        string? error = null)
    {
        lock (statisticsSync)
        {
            if (arp)
            {
                arpStatistics = statistics;
                arpStatisticsError = error;
            }
            else
            {
                forwardingStatistics = statistics;
                forwardingStatisticsError = error;
            }
        }
    }

    public DeviceCaptureCoverage GetCoverage(PhysicalAddress mac) =>
        deviceCoverage.TryGetValue(mac.ToString(), out var coverage)
            ? coverage.Snapshot()
            : new DeviceCaptureCoverage();

    public CaptureDiagnosticsSnapshot Snapshot()
    {
        lock (statisticsSync)
        {
            return new CaptureDiagnosticsSnapshot
            {
                CapturedIpv4Frames = Interlocked.Read(ref capturedIpv4Frames),
                CapturedIpv6Frames = Interlocked.Read(ref capturedIpv6Frames),
                CapturedOtherFrames = Interlocked.Read(ref capturedOtherFrames),
                CapturedMalformedFrames = Interlocked.Read(ref capturedMalformedFrames),
                CapturedArpFrames = Interlocked.Read(ref capturedArpFrames),
                RoutedUploadPayloadBytes = Interlocked.Read(ref routedUploadPayloadBytes),
                RoutedDownloadPayloadBytes = Interlocked.Read(ref routedDownloadPayloadBytes),
                LocalUploadPayloadBytes = Interlocked.Read(ref localUploadPayloadBytes),
                LocalDownloadPayloadBytes = Interlocked.Read(ref localDownloadPayloadBytes),
                ForwardedPackets = Interlocked.Read(ref forwardedPackets),
                DroppedPackets = Interlocked.Read(ref droppedPackets),
                ArpInjectionFailures = Interlocked.Read(ref arpInjectionFailures),
                ForwardingInjectionFailures = Interlocked.Read(ref forwardingInjectionFailures),
                CaptureReadFailures = Interlocked.Read(ref captureReadFailures),
                MappingAmbiguities = Interlocked.Read(ref mappingAmbiguities),
                RejectedMappings = Interlocked.Read(ref rejectedMappings),
                IgnoredFrames = new Dictionary<FrameIgnoreReason, long>(ignoredFrames),
                RejectedMappingReasons = new Dictionary<ClientMappingRejection, long>(rejectedMappingReasons),
                ForwardingStatistics = forwardingStatistics,
                ArpStatistics = arpStatistics,
                ForwardingStatisticsError = forwardingStatisticsError,
                ArpStatisticsError = arpStatisticsError,
            };
        }
    }

    private sealed class CoverageCounters
    {
        private long uploadPackets;
        private long downloadPackets;
        private long droppedPackets;
        private long uploadPayloadBytes;
        private long downloadPayloadBytes;
        private int isLocalComputer;

        public void Record(TrafficDirection direction, int payload, bool dropped, bool local)
        {
            if (direction == TrafficDirection.Upload)
            {
                Interlocked.Increment(ref uploadPackets);
                Interlocked.Add(ref uploadPayloadBytes, payload);
            }
            else
            {
                Interlocked.Increment(ref downloadPackets);
                Interlocked.Add(ref downloadPayloadBytes, payload);
            }

            if (dropped)
            {
                Interlocked.Increment(ref droppedPackets);
            }

            if (local)
            {
                Volatile.Write(ref isLocalComputer, 1);
            }
        }

        public DeviceCaptureCoverage Snapshot() => new(
            Interlocked.Read(ref uploadPackets),
            Interlocked.Read(ref downloadPackets),
            Interlocked.Read(ref droppedPackets),
            Interlocked.Read(ref uploadPayloadBytes),
            Interlocked.Read(ref downloadPayloadBytes),
            Volatile.Read(ref isLocalComputer) != 0);
    }
}

public static class CaptureDiagnosticsFormatter
{
    public static string Format(CaptureDiagnosticsSnapshot snapshot)
    {
        var ignored = string.Join(", ", snapshot.IgnoredFrames.OrderBy(pair => pair.Key)
            .Select(pair => $"{pair.Key} {Number(pair.Value)}"));
        var ambiguity = snapshot.MappingAmbiguities > 0
            ? " Latest-address-only mapping is ambiguous; multi-address/proxy clients may be incomplete."
            : string.Empty;
        var rejections = string.Join(", ", snapshot.RejectedMappingReasons.OrderBy(pair => pair.Key)
            .Select(pair => $"{pair.Key} {Number(pair.Value)}"));
        var capacityWarning = snapshot.RejectedMappingReasons.ContainsKey(ClientMappingRejection.RestorationCapacityReached)
            ? " Restoration journal full: additional interception is skipped."
            : string.Empty;
        return "Observed IPv4 payload only — not complete router/billing usage. IPv6 is observed, never forwarded or counted; no backfill.\n" +
               $"Captured IPv4 {Number(snapshot.CapturedIpv4Frames)}, IPv6 {Number(snapshot.CapturedIpv6Frames)}, " +
               $"ARP {Number(snapshot.CapturedArpFrames)}, other/malformed {Number(snapshot.CapturedOtherFrames + snapshot.CapturedMalformedFrames)}. " +
               $"Ignored by LAN forwarder: {(ignored.Length == 0 ? "none yet" : ignored)}.\n" +
               $"Legacy payload bytes: routed ↓{Number(snapshot.RoutedDownloadPayloadBytes)} ↑{Number(snapshot.RoutedUploadPayloadBytes)}; " +
               $"original local-PC IPv4 ↓{Number(snapshot.LocalDownloadPayloadBytes)} ↑{Number(snapshot.LocalUploadPayloadBytes)} (public off-subnet peers only).\n" +
               $"Forwarded {Number(snapshot.ForwardedPackets)}, policy drops {Number(snapshot.DroppedPackets)}, " +
               $"injection failures ARP/data {Number(snapshot.ArpInjectionFailures)}/{Number(snapshot.ForwardingInjectionFailures)}, " +
               $"capture read failures {Number(snapshot.CaptureReadFailures)}. Counted payload is not proof of delivery.\n" +
               $"Mappings/interception rejected {Number(snapshot.RejectedMappings)} ({(rejections.Length == 0 ? "none" : rejections)}), " +
               $"address/owner changes {Number(snapshot.MappingAmbiguities)}, restore-only journal {Number(snapshot.RestorationTargets)} (not a routing source).{ambiguity}{capacityWarning}\n" +
               $"Capture stats (received/dropped/interface dropped): data {Statistics(snapshot.ForwardingStatistics, snapshot.ForwardingStatisticsError)}; " +
               $"ARP {Statistics(snapshot.ArpStatistics, snapshot.ArpStatisticsError)}. Driver counters are per handle, not added together.";
    }

    private static string Statistics(CaptureHandleStatistics? statistics, string? error) =>
        statistics is null
            ? $"unavailable{(string.IsNullOrWhiteSpace(error) ? string.Empty : $" ({error})")}"
            : $"{Number(statistics.ReceivedPackets)}/{Number(statistics.DroppedPackets)}/{Number(statistics.InterfaceDroppedPackets)}";

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
