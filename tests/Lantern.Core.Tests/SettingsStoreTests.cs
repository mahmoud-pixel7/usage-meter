using System.Security.AccessControl;
using System.Security.Principal;
using Lantern.Core.Settings;

namespace Lantern.Core.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsRulesByNormalizedMac()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new SettingsStore(directory);
            var settings = new AppSettings
            {
                Devices =
                {
                    ["e2:61:19:0d:bd:54"] = new DevicePreferences
                    {
                        Alias = "Living room",
                        LearnedHostName = "POCO-F6",
                        DownloadKiloBytesPerSecond = 500,
                        UploadKiloBytesPerSecond = 100,
                        PauseInternet = true,
                        LastKnownIp = "192.168.31.213",
                        LastSeenUtc = DateTimeOffset.Parse("2026-11-16T10:00:00+02:00"),
                    },
                },
            };

            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();

            var saved = Assert.Single(loaded.Devices);
            Assert.Equal("E261190DBD54", saved.Key);
            Assert.Equal("Living room", saved.Value.Alias);
            Assert.Equal("POCO-F6", saved.Value.LearnedHostName);
            Assert.Equal(500, saved.Value.DownloadKiloBytesPerSecond);
            Assert.Equal(100, saved.Value.UploadKiloBytesPerSecond);
            Assert.True(saved.Value.PauseInternet);
            Assert.Equal("192.168.31.213", saved.Value.LastKnownIp);
            Assert.Equal(DateTimeOffset.Parse("2026-11-16T08:00:00Z"), saved.Value.LastSeenUtc);
            Assert.Equal(TimeSpan.Zero, saved.Value.LastSeenUtc?.Offset);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_DropsInvalidLastKnownIp()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new SettingsStore(directory);
            var settings = new AppSettings
            {
                Devices =
                {
                    ["0E4F69CCE4F0"] = new DevicePreferences
                    {
                        LastKnownIp = "not-an-ip",
                    },
                },
            };

            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();

            Assert.Null(Assert.Single(loaded.Devices).Value.LastKnownIp);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_PersistsNormalizedPerDeviceDomainBlocks()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var blockedDomainsProperty = typeof(AppSettings).GetProperty("BlockedDomains");
            Assert.NotNull(blockedDomainsProperty);
            var settings = new AppSettings();
            var blockedDomains = Assert.IsAssignableFrom<IDictionary<string, List<string>>>(
                blockedDomainsProperty.GetValue(settings));
            blockedDomains["e2:61:19:0d:bd:54"] =
                [" YouTube.COM. ", "youtube.com", "not a domain"];
            var store = new SettingsStore(directory);

            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();

            var loadedDomains = Assert.IsAssignableFrom<IDictionary<string, List<string>>>(
                blockedDomainsProperty.GetValue(loaded));
            var saved = Assert.Single(loadedDomains);
            Assert.Equal("E261190DBD54", saved.Key);
            Assert.Equal(["youtube.com"], saved.Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_PersistsAppliedPresetIdentityAndRestoresItsDomains()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var settings = new AppSettings
            {
                AppliedDomainPresets =
                {
                    ["e2:61:19:0d:bd:54"] = [" youtube ", "YouTube", "unknown"],
                },
            };
            var store = new SettingsStore(directory);

            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();

            var presets = Assert.Single(loaded.AppliedDomainPresets);
            Assert.Equal("E261190DBD54", presets.Key);
            Assert.Equal(["YouTube"], presets.Value);
            var domains = Assert.Single(loaded.BlockedDomains);
            Assert.Contains("youtube.com", domains.Value);
            Assert.Contains("googlevideo.com", domains.Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_PrimaryFileTemporarilyLocked_RestoresTheLastSavedSettings()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new SettingsStore(directory);
            await store.SaveAsync(new AppSettings
            {
                Devices =
                {
                    ["0E4F69CCE4F0"] = new DevicePreferences
                    {
                        LearnedHostName = "POCO-F6",
                        DownloadKiloBytesPerSecond = 500,
                        UploadKiloBytesPerSecond = 100,
                    },
                },
                BlockedDomains =
                {
                    ["0E4F69CCE4F0"] = ["youtube.com"],
                },
            });

            await using var exclusiveLock = new FileStream(
                Path.Combine(directory, "settings.json"),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            var loaded = await store.LoadAsync();

            var device = Assert.Single(loaded.Devices).Value;
            Assert.Equal("POCO-F6", device.LearnedHostName);
            Assert.Equal(500, device.DownloadKiloBytesPerSecond);
            Assert.Equal(100, device.UploadKiloBytesPerSecond);
            Assert.Equal(["youtube.com"], Assert.Single(loaded.BlockedDomains).Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_PreBackupSettingsTemporarilyLocked_WaitsForTheExistingSettingsFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(
                settingsPath,
                """
                {
                  "devices": {
                    "0E4F69CCE4F0": {
                      "learnedHostName": "POCO-F6",
                      "downloadKiloBytesPerSecond": 500,
                      "uploadKiloBytesPerSecond": 100,
                      "pauseInternet": false,
                      "lastKnownIp": "192.168.31.213"
                    }
                  },
                  "blockedDomains": {
                    "0E4F69CCE4F0": ["youtube.com"]
                  },
                  "appliedDomainPresets": {}
                }
                """);
            var exclusiveLock = new FileStream(
                settingsPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            var store = new SettingsStore(directory);

            var loadTask = store.LoadAsync();
            await Task.Delay(100);
            await exclusiveLock.DisposeAsync();
            var loaded = await loadTask;

            Assert.Equal("POCO-F6", Assert.Single(loaded.Devices).Value.LearnedHostName);
            Assert.Equal(["youtube.com"], Assert.Single(loaded.BlockedDomains).Value);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_MalformedSettingsThrowWithoutReplacingOriginalFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            const string broken = "{ definitely not json";
            await File.WriteAllTextAsync(path, broken);
            var store = new SettingsStore(directory);

            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

            Assert.Equal(broken, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_CorruptPrimaryRestoresAliasesFromBackup()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new SettingsStore(directory);
            await store.SaveAsync(new AppSettings
            {
                Devices = { ["021122334455"] = new DevicePreferences { Alias = "My phone" } },
            });
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{ broken");

            var restored = await store.LoadAsync();

            Assert.Equal("My phone", Assert.Single(restored.Devices).Value.Alias);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_NullDeviceMapDoesNotSilentlyDiscardAliases()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{\"devices\":null}");

            await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsStore(directory).LoadAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_MissingPrimaryRestoresAliasAndLastSeenFromBackupWithoutWritingFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var backupPath = Path.Combine(directory, "settings.backup.json");
            const string json = """
                {
                  "devices": {
                    "02:11:22:33:44:55": {
                      "alias": " My phone ",
                      "learnedHostName": " Pixel ",
                      "lastKnownIp": "192.168.31.50",
                      "lastSeenUtc": "2026-11-16T10:00:00+02:00"
                    }
                  }
                }
                """;
            await File.WriteAllTextAsync(backupPath, json);

            var loaded = await new SettingsStore(directory).LoadAsync();

            var device = Assert.Single(loaded.Devices);
            Assert.Equal("021122334455", device.Key);
            Assert.Equal("My phone", device.Value.Alias);
            Assert.Equal("Pixel", device.Value.LearnedHostName);
            Assert.Equal("192.168.31.50", device.Value.LastKnownIp);
            Assert.Equal(DateTimeOffset.Parse("2026-11-16T08:00:00Z"), device.Value.LastSeenUtc);
            Assert.Equal(TimeSpan.Zero, device.Value.LastSeenUtc?.Offset);
            Assert.Equal(json, await File.ReadAllTextAsync(backupPath));
            Assert.Equal(backupPath, Assert.Single(Directory.GetFileSystemEntries(directory)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_BothCopiesCorrupt_ThrowsWithoutChangingEitherFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var primaryPath = Path.Combine(directory, "settings.json");
            var backupPath = Path.Combine(directory, "settings.backup.json");
            await File.WriteAllTextAsync(primaryPath, "{ broken primary");
            await File.WriteAllTextAsync(backupPath, "{ broken backup");
            var primaryBytes = await File.ReadAllBytesAsync(primaryPath);
            var backupBytes = await File.ReadAllBytesAsync(backupPath);

            await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsStore(directory).LoadAsync());

            Assert.Equal(primaryBytes, await File.ReadAllBytesAsync(primaryPath));
            Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backupPath));
            Assert.Equal(new[] { backupPath, primaryPath },
                Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Load_DirectoryInPlaceOfASettingsCopy_ThrowsWithoutChangingIt(
        bool primaryIsDirectory,
        bool backupIsDirectory)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var paths = new List<string>();
            if (primaryIsDirectory)
            {
                paths.Add(Path.Combine(directory, "settings.json"));
            }

            if (backupIsDirectory)
            {
                paths.Add(Path.Combine(directory, "settings.backup.json"));
            }

            foreach (var path in paths)
            {
                Directory.CreateDirectory(path);
                await File.WriteAllTextAsync(Path.Combine(path, "keep.txt"), "do not replace");
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsStore(directory).LoadAsync());

            Assert.Equal(paths.Order(StringComparer.Ordinal),
                Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal));
            foreach (var path in paths)
            {
                var marker = Assert.Single(Directory.GetFileSystemEntries(path));
                Assert.Equal("do not replace", await File.ReadAllTextAsync(marker));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_PrimaryPathIsDirectory_RestoresBackupWithoutChangingEitherPath()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var primaryPath = Path.Combine(directory, "settings.json");
            Directory.CreateDirectory(primaryPath);
            var markerPath = Path.Combine(primaryPath, "keep.txt");
            await File.WriteAllTextAsync(markerPath, "do not replace");
            var backupPath = Path.Combine(directory, "settings.backup.json");
            const string json = "{\"devices\":{\"021122334455\":{\"alias\":\"My phone\"}}}";
            await File.WriteAllTextAsync(backupPath, json);

            var loaded = await new SettingsStore(directory).LoadAsync();

            Assert.Equal("My phone", Assert.Single(loaded.Devices).Value.Alias);
            Assert.Equal("do not replace", await File.ReadAllTextAsync(markerPath));
            Assert.Equal(json, await File.ReadAllTextAsync(backupPath));
            Assert.Equal(new[] { backupPath, primaryPath },
                Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_SettingsParentIsAFile_ThrowsWithoutReplacingIt()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var parentPath = Path.Combine(directory, "settings-directory");
            const string content = "this is not a directory";
            await File.WriteAllTextAsync(parentPath, content);

            await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsStore(parentPath).LoadAsync());

            Assert.Equal(content, await File.ReadAllTextAsync(parentPath));
            Assert.Equal(parentPath, Assert.Single(Directory.GetFileSystemEntries(directory)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("settings.backup.json")]
    public async Task Load_OnlyExistingCopyLocked_ThrowsInsteadOfReturningFreshSettings(string fileName)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, fileName);
            const string json = "{\"devices\":{\"021122334455\":{\"alias\":\"My phone\"}}}";
            await File.WriteAllTextAsync(path, json);
            await using (var exclusiveLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsStore(directory).LoadAsync());
            }

            Assert.Equal(json, await File.ReadAllTextAsync(path));
            Assert.Equal(path, Assert.Single(Directory.GetFileSystemEntries(directory)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_BothCopiesLocked_ThrowsWithoutChangingEitherFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var primaryPath = Path.Combine(directory, "settings.json");
            var backupPath = Path.Combine(directory, "settings.backup.json");
            const string json = "{\"devices\":{\"021122334455\":{\"alias\":\"My phone\"}}}";
            await File.WriteAllTextAsync(primaryPath, json);
            await File.WriteAllTextAsync(backupPath, json);
            await using (var primaryLock = new FileStream(primaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await using (var backupLock = new FileStream(backupPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsStore(directory).LoadAsync());
            }

            Assert.Equal(json, await File.ReadAllTextAsync(primaryPath));
            Assert.Equal(json, await File.ReadAllTextAsync(backupPath));
            Assert.Equal(new[] { backupPath, primaryPath },
                Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_CopyDisappearsAfterAReadFailure_DoesNotTreatItAsFresh()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, "{}");
            await using (var exclusiveLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete))
            {
                var load = new SettingsStore(directory).LoadAsync();
                File.Delete(path);

                await Assert.ThrowsAsync<InvalidDataException>(() => load);
            }

            Assert.Empty(Directory.GetFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_UnreadableParentDirectory_ThrowsWithoutReplacingFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTemporaryDirectory();
        try
        {
            var directory = Path.Combine(root, "settings");
            Directory.CreateDirectory(directory);
            var primaryPath = Path.Combine(directory, "settings.json");
            var backupPath = Path.Combine(directory, "settings.backup.json");
            const string json = "{\"devices\":{\"021122334455\":{\"alias\":\"My phone\"}}}";
            await File.WriteAllTextAsync(primaryPath, json);
            await File.WriteAllTextAsync(backupPath, json);
            var directoryInfo = new DirectoryInfo(directory);
            var originalSecurity = directoryInfo.GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorBinaryForm();
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User;
            Assert.NotNull(user);
            var restrictedSecurity = new DirectorySecurity();
            restrictedSecurity.SetSecurityDescriptorBinaryForm(originalSecurity, AccessControlSections.Access);
            restrictedSecurity.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.ReadData | FileSystemRights.ReadAttributes,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Deny));
            try
            {
                directoryInfo.SetAccessControl(restrictedSecurity);

                await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsStore(directory).LoadAsync());
            }
            finally
            {
                var restoredSecurity = new DirectorySecurity();
                restoredSecurity.SetSecurityDescriptorBinaryForm(originalSecurity, AccessControlSections.Access);
                directoryInfo.SetAccessControl(restoredSecurity);
            }

            Assert.Equal(json, await File.ReadAllTextAsync(primaryPath));
            Assert.Equal(json, await File.ReadAllTextAsync(backupPath));
            Assert.Equal(new[] { backupPath, primaryPath },
                Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Load_BothCopiesMissing_ReturnsFreshSettingsWithoutCreatingFiles(bool directoryIsMissing)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var directory = directoryIsMissing ? Path.Combine(root, "not-created", "settings") : root;

            var loaded = await new SettingsStore(directory).LoadAsync();

            Assert.Empty(loaded.Devices);
            Assert.Empty(loaded.BlockedDomains);
            Assert.Empty(loaded.AppliedDomainPresets);
            Assert.False(loaded.DisableUpdateChecks);
            Assert.Null(loaded.LastUpdateCheckUtc);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"futureProperty\":{\"enabled\":true}}")]
    public async Task Load_LegacyEmptyAndUnknownPropertiesRemainValidWithoutWritingFiles(string json)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, json);

            var loaded = await new SettingsStore(directory).LoadAsync();

            Assert.Empty(loaded.Devices);
            Assert.Empty(loaded.BlockedDomains);
            Assert.Empty(loaded.AppliedDomainPresets);
            Assert.Equal(json, await File.ReadAllTextAsync(path));
            Assert.Equal(path, Assert.Single(Directory.GetFileSystemEntries(directory)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"blockedDomains\":null}")]
    [InlineData("{\"appliedDomainPresets\":null}")]
    [InlineData("{\"devices\":{\"021122334455\":null}}")]
    public async Task Load_NullSettingsOrCollections_ThrowWithoutReplacingTheFile(string json)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, json);

            await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsStore(directory).LoadAsync());

            Assert.Equal(json, await File.ReadAllTextAsync(path));
            Assert.Equal(path, Assert.Single(Directory.GetFileSystemEntries(directory)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_PersistsUpdateCheckPreferences()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var checkedAt = new DateTimeOffset(2026, 8, 1, 12, 30, 0, TimeSpan.FromHours(2));
            var store = new SettingsStore(directory);
            var settings = new AppSettings
            {
                DisableUpdateChecks = true,
                LastUpdateCheckUtc = checkedAt,
            };

            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();

            Assert.True(loaded.DisableUpdateChecks);
            Assert.Equal(checkedAt.ToUniversalTime(), loaded.LastUpdateCheckUtc);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentStores_SerializeWritesToTheSameSettingsPath()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var first = new SettingsStore(directory);
            var second = new SettingsStore(directory);
            var writes = Enumerable.Range(0, 40)
                .Select(index => (index & 1) == 0
                    ? first.SaveAsync(new AppSettings { DisableUpdateChecks = true })
                    : second.SaveAsync(new AppSettings { DisableUpdateChecks = false }));

            await Task.WhenAll(writes);

            var loaded = await first.LoadAsync();
            Assert.NotNull(loaded);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentLoadAndSave_DoNotRaceReplacingSettingsFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(
                settingsPath,
                $"{{\"padding\":\"{new string('x', 16 * 1024 * 1024)}\"}}");
            var store = new SettingsStore(directory);

            var load = store.LoadAsync();
            var save = store.SaveAsync(new AppSettings { DisableUpdateChecks = true });

            await Task.WhenAll(load, save);
            var loaded = await store.LoadAsync();
            Assert.True(loaded.DisableUpdateChecks);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"LanternControlTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
