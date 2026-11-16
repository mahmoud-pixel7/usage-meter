using Lantern.App.Services;
using Xunit;

namespace Lantern.App.Tests;

public sealed class EngineStopStatusTests
{
    [Fact]
    public void SuccessfulSends_DoNotClaimVerifiedPeerRestoration()
    {
        var status = EngineStopStatus.Format(true, []);

        Assert.Contains("Corrective IPv4 ARP frames were sent", status);
        Assert.Contains("peer acceptance cannot be verified", status);
        Assert.DoesNotContain("safe", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RestorationOrCaptureStopFailures_RemainInTheFinalStatus()
    {
        var status = EngineStopStatus.Format(true,
            ["ARP restoration failed", "ARP capture stop reported an error", "Data handle close failed"]);

        Assert.Contains("incomplete or unverified", status);
        Assert.Contains("ARP restoration failed", status);
        Assert.Contains("ARP capture stop reported an error", status);
        Assert.Contains("Data handle close failed", status);
        Assert.DoesNotContain("frames were sent", status);
        Assert.DoesNotContain("safe", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoRecordedClients_DoesNotClaimCorrectiveFramesWereSent()
    {
        var status = EngineStopStatus.Format(false, []);

        Assert.Contains("No client IPv4 ARP mappings were available", status);
        Assert.DoesNotContain("frames were sent", status);
    }

    [Fact]
    public void EngineFailureAndRestorationFailure_AreBothPreserved()
    {
        var status = EngineStopStatus.Format(true, ["restoration injection failed"], "forwarding read failed");

        Assert.Contains("forwarding read failed", status);
        Assert.Contains("restoration injection failed", status);
    }
}
