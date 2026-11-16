using System.Net;
using System.Net.NetworkInformation;
using Lantern.App.Services;
using Lantern.Core.Settings;
using Xunit;

namespace Lantern.App.Tests;

public sealed class DeviceNamingTests
{
    private static readonly PhysicalAddress FirstMac = PhysicalAddress.Parse("0E4F69CCE4F0");
    private static readonly PhysicalAddress SecondMac = PhysicalAddress.Parse("D2574CDCA5B2");

    [Fact]
    public void KnownHints_UseOnlyLearnedNamesAndNeverAliases()
    {
        var settings = new AppSettings
        {
            Devices =
            {
                [FirstMac.ToString()] = new DevicePreferences
                {
                    Alias = "My phone",
                    LearnedHostName = " Pixel-10-Pro ",
                    LastKnownIp = "192.168.31.213",
                },
                [SecondMac.ToString()] = new DevicePreferences
                {
                    Alias = "Living room",
                    LastKnownIp = "192.168.31.225",
                },
            },
        };

        var hints = KnownDeviceHintFactory.Build(settings);

        Assert.Equal("Pixel-10-Pro", hints.Single(hint => hint.MacAddress.Equals(FirstMac)).HostName);
        var aliasOnly = hints.Single(hint => hint.MacAddress.Equals(SecondMac));
        Assert.Null(aliasOnly.HostName);
        Assert.Equal(IPAddress.Parse("192.168.31.225"), aliasOnly.LastKnownIp);
        Assert.Equal("My phone", settings.Devices[FirstMac.ToString()].Alias);
        Assert.Equal(" Pixel-10-Pro ", settings.Devices[FirstMac.ToString()].LearnedHostName);
    }

    [Fact]
    public void KnownHints_KeepDuplicateLearnedNamesBoundToTheirOwnMacs()
    {
        var settings = new AppSettings
        {
            Devices =
            {
                [FirstMac.ToString()] = new DevicePreferences
                {
                    Alias = "First phone",
                    LearnedHostName = " Android ",
                    LastKnownIp = "192.168.31.213",
                },
                [SecondMac.ToString()] = new DevicePreferences
                {
                    LearnedHostName = "android",
                    LastKnownIp = "192.168.31.225",
                },
            },
        };

        var hints = KnownDeviceHintFactory.Build(settings);

        Assert.Equal(2, hints.Count);
        Assert.Equal("Android", hints.Single(hint => hint.MacAddress.Equals(FirstMac)).HostName);
        Assert.Equal("android", hints.Single(hint => hint.MacAddress.Equals(SecondMac)).HostName);
        Assert.Equal(IPAddress.Parse("192.168.31.213"),
            hints.Single(hint => hint.MacAddress.Equals(FirstMac)).LastKnownIp);
        Assert.Equal(IPAddress.Parse("192.168.31.225"),
            hints.Single(hint => hint.MacAddress.Equals(SecondMac)).LastKnownIp);
        Assert.Contains("Android", KnownDeviceHintFactory.FindAmbiguousLearnedNames(settings));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void KnownHints_DoNotSubstituteAnAliasForAMissingLearnedName(string? name)
    {
        var settings = new AppSettings
        {
            Devices =
            {
                [FirstMac.ToString()] = new DevicePreferences
                {
                    Alias = "A manually assigned alias",
                    LearnedHostName = name,
                },
            },
        };

        Assert.Null(Assert.Single(KnownDeviceHintFactory.Build(settings)).HostName);
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("2001:db8::1")]
    public void KnownHints_IgnoreInvalidOrNonIpv4AddressHints(string address)
    {
        var settings = new AppSettings
        {
            Devices =
            {
                [FirstMac.ToString()] = new DevicePreferences
                {
                    LearnedHostName = "Phone",
                    LastKnownIp = address,
                },
            },
        };

        var hint = Assert.Single(KnownDeviceHintFactory.Build(settings));

        Assert.Null(hint.LastKnownIp);
        Assert.Equal("Phone", hint.HostName);
    }

    [Fact]
    public void KnownHints_IgnoreMalformedAndNonEthernetMacs()
    {
        var settings = new AppSettings
        {
            Devices =
            {
                ["not-a-mac"] = new DevicePreferences(),
                ["AA"] = new DevicePreferences(),
                [FirstMac.ToString()] = new DevicePreferences(),
            },
        };

        Assert.Equal(FirstMac, Assert.Single(KnownDeviceHintFactory.Build(settings)).MacAddress);
    }

    [Fact]
    public void Claims_AllowDuplicateNamesIncludingTheControllersName()
    {
        var claims = new ResolvedDeviceNameClaims("Android");

        Assert.True(claims.TryClaim(FirstMac, " Android ", out var first, out var firstIsNew));
        Assert.True(claims.TryClaim(SecondMac, "Android", out var second, out var secondIsNew));

        Assert.Equal("Android", first);
        Assert.Equal("Android", second);
        Assert.True(firstIsNew);
        Assert.True(secondIsNew);
        Assert.True(claims.TryClaim(FirstMac, "android", out _, out var repeated));
        Assert.False(repeated);
    }

    [Fact]
    public void Claims_LegacyGlobalRejectionListDoesNotExcludeOtherDevices()
    {
        var claims = new ResolvedDeviceNameClaims("Controller");
        Assert.True(claims.TryClaim(FirstMac, "Android", out _));

        claims.Reset(["Android", "Controller"]);

        Assert.True(claims.TryClaim(FirstMac, "Android", out _, out var resetClaim));
        Assert.True(resetClaim);
        Assert.True(claims.TryClaim(SecondMac, "Controller", out _));
    }

    [Fact]
    public void Claims_ReportNameChangesPerMacWithoutAffectingOtherMacs()
    {
        var claims = new ResolvedDeviceNameClaims("Controller");
        Assert.True(claims.TryClaim(FirstMac, "Phone", out _, out var first));
        Assert.True(first);
        Assert.True(claims.TryClaim(SecondMac, "Phone", out _, out var second));
        Assert.True(second);

        Assert.True(claims.TryClaim(FirstMac, "New-phone", out var renamed, out var changed));
        Assert.Equal("New-phone", renamed);
        Assert.True(changed);
        Assert.True(claims.TryClaim(SecondMac, "Phone", out _, out var unchanged));
        Assert.False(unchanged);
        Assert.True(claims.TryClaim(FirstMac, "Phone", out _, out var reverted));
        Assert.True(reverted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("192.168.31.213")]
    [InlineData("2001:db8::1")]
    [InlineData("Phone\0other")]
    [InlineData("Phone\nother")]
    public void Claims_RejectEmptyIpAndControlCharacterNamesWithoutClaimingThem(string? candidate)
    {
        var claims = new ResolvedDeviceNameClaims("Controller");

        Assert.False(claims.TryClaim(FirstMac, candidate, out var accepted, out var isNew));

        Assert.Equal(string.Empty, accepted);
        Assert.False(isNew);
        Assert.True(claims.TryClaim(FirstMac, "Phone", out _, out var validIsNew));
        Assert.True(validIsNew);
    }

    [Fact]
    public void Claims_ReportOnlyOneNewClaimForConcurrentRepeats()
    {
        var claims = new ResolvedDeviceNameClaims("Controller");
        var newClaims = 0;

        Parallel.For(0, 32, iteration =>
        {
            Assert.True(claims.TryClaim(FirstMac, "Phone", out _, out var isNew));
            if (isNew)
            {
                Interlocked.Increment(ref newClaims);
            }
        });

        Assert.Equal(1, newClaims);
    }
}
