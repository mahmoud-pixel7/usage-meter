using Lantern.Core.Settings;

namespace Lantern.Core.Tests;

public sealed class DeviceIdentityTrackerTests
{
    [Fact]
    public void Learn_DoesNotMigrateAUniqueSavedProfileToAnotherMac()
    {
        var saved = new DevicePreferences
        {
            Alias = "Omar's phone",
            LearnedHostName = "POCO-F6",
            DownloadKiloBytesPerSecond = 100,
            UploadKiloBytesPerSecond = 25,
            PauseInternet = true,
            LastKnownIp = "192.168.31.213",
        };
        var current = new DevicePreferences { LastKnownIp = "192.168.31.50" };
        var savedDomains = new List<string> { "example.com" };
        var savedPresets = new List<string> { "social" };
        var settings = new AppSettings
        {
            Devices =
            {
                ["0E4F69CCE4F0"] = saved,
                ["AA7051600355"] = current,
            },
            BlockedDomains = { ["0E4F69CCE4F0"] = savedDomains },
            AppliedDomainPresets = { ["0E4F69CCE4F0"] = savedPresets },
        };

        var result = DeviceIdentityTracker.Learn(
            settings,
            "AA7051600355",
            "POCO-F6",
            "192.168.31.50");

        Assert.Null(result.PreviousMacKey);
        Assert.Equal("AA7051600355", result.MacKey);
        Assert.Equal(2, settings.Devices.Count);
        Assert.Same(saved, settings.Devices["0E4F69CCE4F0"]);
        Assert.Equal("Omar's phone", saved.Alias);
        Assert.Equal("POCO-F6", saved.LearnedHostName);
        Assert.Equal(100, saved.DownloadKiloBytesPerSecond);
        Assert.Equal(25, saved.UploadKiloBytesPerSecond);
        Assert.True(saved.PauseInternet);
        Assert.Equal("192.168.31.213", saved.LastKnownIp);
        Assert.Same(savedDomains, settings.BlockedDomains["0E4F69CCE4F0"]);
        Assert.Same(savedPresets, settings.AppliedDomainPresets["0E4F69CCE4F0"]);
        Assert.False(settings.BlockedDomains.ContainsKey("AA7051600355"));
        Assert.False(settings.AppliedDomainPresets.ContainsKey("AA7051600355"));
        Assert.Same(current, result.Preferences);
        Assert.Same(current, settings.Devices["AA7051600355"]);
        Assert.Null(current.Alias);
        Assert.Equal("POCO-F6", current.LearnedHostName);
        Assert.Equal(0, current.DownloadKiloBytesPerSecond);
        Assert.Equal(0, current.UploadKiloBytesPerSecond);
        Assert.False(current.PauseInternet);
        Assert.Equal("192.168.31.50", current.LastKnownIp);
    }

    [Fact]
    public void Learn_AllowsTheSameHostNameOnMultipleMacs()
    {
        var settings = new AppSettings
        {
            Devices =
            {
                ["0E4F69CCE4F0"] = new DevicePreferences
                {
                    Alias = "First phone",
                    LearnedHostName = "Android",
                },
                ["D2574CDCA5B2"] = new DevicePreferences
                {
                    Alias = "Second phone",
                    LearnedHostName = "Android",
                },
            },
        };

        var result = DeviceIdentityTracker.Learn(
            settings,
            "AA7051600355",
            "Android",
            "192.168.31.50");

        Assert.Null(result.PreviousMacKey);
        Assert.Equal(3, settings.Devices.Count);
        Assert.Equal("First phone", settings.Devices["0E4F69CCE4F0"].Alias);
        Assert.Equal("Second phone", settings.Devices["D2574CDCA5B2"].Alias);
        Assert.Null(result.Preferences.Alias);
        Assert.All(settings.Devices.Values, profile => Assert.Equal("Android", profile.LearnedHostName));
    }

    [Fact]
    public void Learn_UpdatesOnlyMetadataForTheExistingMac()
    {
        var profile = new DevicePreferences
        {
            Alias = "My phone",
            LearnedHostName = "Previous name",
            DownloadKiloBytesPerSecond = 100,
            UploadKiloBytesPerSecond = 50,
            PauseInternet = true,
        };
        var settings = new AppSettings { Devices = { ["0E4F69CCE4F0"] = profile } };

        var result = DeviceIdentityTracker.Learn(
            settings,
            "0e:4f:69:cc:e4:f0",
            " POCO-F6 ",
            "192.168.31.99");

        Assert.Null(result.PreviousMacKey);
        Assert.Equal("0E4F69CCE4F0", result.MacKey);
        Assert.Single(settings.Devices);
        Assert.Same(profile, result.Preferences);
        Assert.Same(profile, settings.Devices["0E4F69CCE4F0"]);
        Assert.Equal("My phone", profile.Alias);
        Assert.Equal(100, profile.DownloadKiloBytesPerSecond);
        Assert.Equal(50, profile.UploadKiloBytesPerSecond);
        Assert.True(profile.PauseInternet);
        Assert.Equal("POCO-F6", profile.LearnedHostName);
        Assert.Equal("192.168.31.99", profile.LastKnownIp);
    }

    [Fact]
    public void Learn_NewMacDoesNotInheritAnotherMacsAddressHint()
    {
        var saved = new DevicePreferences
        {
            LearnedHostName = "POCO-F6",
            LastKnownIp = "192.168.31.213",
        };
        var settings = new AppSettings { Devices = { ["0E4F69CCE4F0"] = saved } };

        var result = DeviceIdentityTracker.Learn(settings, "AA7051600355", "POCO-F6", null);

        Assert.Null(result.PreviousMacKey);
        Assert.Equal(2, settings.Devices.Count);
        Assert.Same(saved, settings.Devices["0E4F69CCE4F0"]);
        Assert.Equal("192.168.31.213", saved.LastKnownIp);
        Assert.NotSame(saved, result.Preferences);
        Assert.Null(result.Preferences.LastKnownIp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("2001:db8::1")]
    public void Learn_MissingOrInvalidIpv4PreservesOnlyTheCurrentMacsAddressHint(string? address)
    {
        var settings = new AppSettings
        {
            Devices =
            {
                ["0E4F69CCE4F0"] = new DevicePreferences
                {
                    LearnedHostName = "POCO-F6",
                    LastKnownIp = "192.168.31.213",
                },
                ["AA7051600355"] = new DevicePreferences { LastKnownIp = "192.168.31.50" },
            },
        };

        var result = DeviceIdentityTracker.Learn(settings, "AA7051600355", "POCO-F6", address);

        Assert.Null(result.PreviousMacKey);
        Assert.Equal("192.168.31.50", result.Preferences.LastKnownIp);
        Assert.Equal("192.168.31.213", settings.Devices["0E4F69CCE4F0"].LastKnownIp);
        Assert.Equal(2, settings.Devices.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Learn_RejectsAnEmptyNameWithoutChangingSettings(string name)
    {
        var settings = new AppSettings();

        Assert.Throws<ArgumentException>(() =>
            DeviceIdentityTracker.Learn(settings, "AA7051600355", name, "192.168.31.50"));

        Assert.Empty(settings.Devices);
    }
}
