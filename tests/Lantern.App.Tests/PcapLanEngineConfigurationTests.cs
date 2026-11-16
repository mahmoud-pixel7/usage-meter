using Lantern.App.Services;
using Lantern.Core.Settings;
using System.Net;
using System.Net.NetworkInformation;
using Xunit;

namespace Lantern.App.Tests;

public sealed class PcapLanEngineConfigurationTests
{
    [Fact]
    public void KnownDeviceHint_CarriesPersistedDhcpHostName()
    {
        var hint = new KnownDeviceHint(
            PhysicalAddress.Parse("0E4F69CCE4F0"),
            IPAddress.Parse("192.168.31.213"),
            "POCO-F6");

        Assert.Equal("POCO-F6", hint.HostName);
    }

    [Fact]
    public void ProbeInterval_MatchesThePreviouslyWorkingScanner()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(10), PcapLanEngine.ProbeInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(800), PcapLanEngine.ProbeReplyWindow);
    }

    [Fact]
    public void AutomaticMaintenance_DoesNotContinuouslySweepTheSubnet()
    {
        Assert.False(PassiveDiscoveryProfile.Default.ProbeSubnetOnRefresh);
        Assert.Equal(TimeSpan.FromSeconds(5), PassiveDiscoveryProfile.Default.RefreshInterval);
    }

    [Fact]
    public void Engine_ReportsRunningStateChangesToTheWindow()
    {
        Assert.NotNull(typeof(PcapLanEngine).GetEvent("StateChanged"));
    }

    [Fact]
    public void ForwardingFilter_ObservesIpv6WithoutAddingIpv6Interception()
    {
        Assert.Equal("ip or ip6", PcapLanEngine.ForwardingFilter);
    }

    [Fact]
    public void Engine_ExposesHonestDiagnosticsAndRejectsUnscopedIdentityEventsWithoutStartingCapture()
    {
        var engine = new PcapLanEngine(new Lantern.Core.Devices.DeviceRegistry(), new Lantern.Core.Control.TrafficPolicy());
        var legacyIdentity = new DeviceIdentityLearnedEventArgs(
            PhysicalAddress.Parse("0E4F69CCE4F0"), "POCO-F6");

        Assert.False(engine.IsRunning);
        Assert.False(engine.IsIdentityCurrent(legacyIdentity));
        Assert.Equal(0, engine.SessionId);
        Assert.Contains("not complete", engine.GetDiagnosticsSummary());
        Assert.Contains("not proof of zero", engine.GetCoverageStatus("0E4F69CCE4F0"));
        Assert.Contains("no network restoration was attempted", engine.StopStatus);
    }

    [Fact]
    public void KnownDeviceHints_KeepPerMacLearnedNamesAndNeverUseAliases()
    {
        var settings = new AppSettings
        {
            Devices =
            {
                ["0E4F69CCE4F0"] = new DevicePreferences
                {
                    Alias = "My phone",
                    LearnedHostName = "Humane",
                    LastKnownIp = "192.168.31.213",
                },
                ["D2574CDCA5B2"] = new DevicePreferences
                {
                    LearnedHostName = "Humane",
                    LastKnownIp = "192.168.31.225",
                },
            },
        };

        var hints = KnownDeviceHintFactory.Build(settings);

        Assert.Equal("Humane", hints.Single(hint =>
            hint.MacAddress.Equals(PhysicalAddress.Parse("0E4F69CCE4F0"))).HostName);
        Assert.Equal("Humane", hints.Single(hint =>
            hint.MacAddress.Equals(PhysicalAddress.Parse("D2574CDCA5B2"))).HostName);
    }

    [Fact]
    public void ResolvedDeviceNames_ArePerMacEvenWhenANameMatchesAnotherDeviceOrController()
    {
        var claims = new ResolvedDeviceNameClaims("Humane");
        var firstMac = PhysicalAddress.Parse("0E4F69CCE4F0");
        var secondMac = PhysicalAddress.Parse("D2574CDCA5B2");

        Assert.True(claims.TryClaim(firstMac, "Humane", out _));
        Assert.True(claims.TryClaim(firstMac, "POCO-F6", out var accepted));
        Assert.Equal("POCO-F6", accepted);
        Assert.True(claims.TryClaim(secondMac, "POCO-F6", out _));
    }

    [Fact]
    public void ResolvedDeviceNames_ReportOnlyTheFirstAcceptedClaimAsNew()
    {
        var claims = new ResolvedDeviceNameClaims("Humane");
        var mac = PhysicalAddress.Parse("0E4F69CCE4F0");

        Assert.True(claims.TryClaim(mac, "POCO-F6", out var accepted, out var firstClaim));
        Assert.Equal("POCO-F6", accepted);
        Assert.True(firstClaim);

        Assert.True(claims.TryClaim(mac, "POCO-F6", out accepted, out var repeatedClaim));
        Assert.Equal("POCO-F6", accepted);
        Assert.False(repeatedClaim);
    }
}
