using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Lantern.Core.Devices;
using Lantern.Core.Settings;

namespace Lantern.App.ViewModels;

public sealed class DeviceViewModel : INotifyPropertyChanged
{
    private readonly Func<DeviceViewModel, Task> changed;
    private string ipAddress = "-";
    private string macAddress = "-";
    private string displayName = "Unknown device";
    private string automaticName = "Unknown device";
    private string? alias;
    private string downloadRate = "0 B/s";
    private string uploadRate = "0 B/s";
    private double downloadBytesPerSecond;
    private double uploadBytesPerSecond;
    private double totalRate;
    private long usageDownloadBytes;
    private long usageUploadBytes;
    private long usageTotalBytes;
    private int downloadLimit;
    private int uploadLimit;
    private bool pauseInternet;
    private bool isProtected;
    private bool isOnline = true;
    private bool isMonitoring = true;
    private DateTimeOffset observedAt;
    private string protectedReason = string.Empty;
    private string captureCoverageText = "Capture-session coverage unavailable.";

    public DeviceViewModel(Func<DeviceViewModel, Task> changed)
    {
        this.changed = changed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string IpAddress
    {
        get => ipAddress;
        private set => SetField(ref ipAddress, value);
    }

    public string MacAddress
    {
        get => macAddress;
        private set => SetField(ref macAddress, value);
    }

    public string MacKey { get; private set; } = string.Empty;

    public string DisplayName
    {
        get => displayName;
        private set
        {
            if (SetField(ref displayName, value))
            {
                OnPropertyChanged(nameof(EditableName));
            }
        }
    }

    public string AutomaticName
    {
        get => automaticName;
        private set => SetField(ref automaticName, value);
    }

    public string? Alias => alias;

    public string EditableName
    {
        get => DisplayName;
        set => EditableAlias = value;
    }

    public string EditableAlias
    {
        get => alias ?? string.Empty;
        set
        {
            if (!CanRename)
            {
                return;
            }

            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(alias, normalized, StringComparison.Ordinal))
            {
                return;
            }

            alias = normalized;
            OnPropertyChanged(nameof(Alias));
            OnPropertyChanged(nameof(EditableAlias));
            DisplayName = alias ?? AutomaticName;
            _ = changed(this);
        }
    }

    public string DownloadRate
    {
        get => downloadRate;
        private set => SetField(ref downloadRate, value);
    }

    public string UploadRate
    {
        get => uploadRate;
        private set => SetField(ref uploadRate, value);
    }

    public double DownloadBytesPerSecond
    {
        get => downloadBytesPerSecond;
        private set => SetField(ref downloadBytesPerSecond, value);
    }

    public double UploadBytesPerSecond
    {
        get => uploadBytesPerSecond;
        private set => SetField(ref uploadBytesPerSecond, value);
    }

    public double TotalRate
    {
        get => totalRate;
        private set => SetField(ref totalRate, value);
    }

    public long UsageDownloadBytes
    {
        get => usageDownloadBytes;
        private set
        {
            if (SetField(ref usageDownloadBytes, value))
            {
                OnPropertyChanged(nameof(UsageDownloadText));
                OnPropertyChanged(nameof(UsageDownloadExactText));
            }
        }
    }

    public long UsageUploadBytes
    {
        get => usageUploadBytes;
        private set
        {
            if (SetField(ref usageUploadBytes, value))
            {
                OnPropertyChanged(nameof(UsageUploadText));
                OnPropertyChanged(nameof(UsageUploadExactText));
            }
        }
    }

    public long UsageTotalBytes
    {
        get => usageTotalBytes;
        private set
        {
            if (SetField(ref usageTotalBytes, value))
            {
                OnPropertyChanged(nameof(UsageTotalText));
                OnPropertyChanged(nameof(UsageTotalExactText));
            }
        }
    }

    public string UsageDownloadText => FormatBytes(UsageDownloadBytes);

    public string UsageUploadText => FormatBytes(UsageUploadBytes);

    public string UsageTotalText => FormatBytes(UsageTotalBytes);

    public string UsageDownloadExactText => FormatExactBytes(UsageDownloadBytes);

    public string UsageUploadExactText => FormatExactBytes(UsageUploadBytes);

    public string UsageTotalExactText => FormatExactBytes(UsageTotalBytes);

    public int DownloadLimit
    {
        get => downloadLimit;
        set
        {
            if (CanControl && SetField(ref downloadLimit, Math.Max(0, value)))
            {
                RaiseRuleStateChanged();
                _ = changed(this);
            }
        }
    }

    public int UploadLimit
    {
        get => uploadLimit;
        set
        {
            if (CanControl && SetField(ref uploadLimit, Math.Max(0, value)))
            {
                RaiseRuleStateChanged();
                _ = changed(this);
            }
        }
    }

    public bool PauseInternet
    {
        get => CanControl && pauseInternet;
        set
        {
            if (CanControl && SetField(ref pauseInternet, value))
            {
                RaiseRuleStateChanged();
                _ = changed(this);
            }
        }
    }

    public bool IsProtected
    {
        get => isProtected;
        private set
        {
            if (SetField(ref isProtected, value))
            {
                RaiseProtectionChanged();
            }
        }
    }

    public bool CanControl => !IsProtected;

    public bool IsLocalComputer =>
        IsProtected && ProtectedReason.StartsWith("This computer", StringComparison.OrdinalIgnoreCase);

    public bool IsGateway => IsProtected && !IsLocalComputer;

    public bool CanRename => !IsGateway;

    public bool IsOnline
    {
        get => isOnline;
        private set
        {
            if (SetField(ref isOnline, value))
            {
                OnPropertyChanged(nameof(PresenceText));
            }
        }
    }

    public string PresenceText => !IsMonitoring
        ? "Monitoring stopped"
        : IsOnline ? "Recently seen" : "Not recently seen";

    public DateTimeOffset ObservedAt
    {
        get => observedAt;
        private set
        {
            if (SetField(ref observedAt, value))
            {
                OnPropertyChanged(nameof(LastSeenText));
            }
        }
    }

    public string LastSeenText => ObservedAt == default
        ? "-"
        : $"Last observed {ObservedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}";

    public bool IsMonitoring => isMonitoring;

    public string MonitoringStateText => IsMonitoring
        ? "Monitoring active"
        : "Monitoring stopped — rates unavailable";

    public string CaptureCoverageText
    {
        get => captureCoverageText;
        private set => SetField(ref captureCoverageText, value);
    }

    public bool HasConfiguredRule =>
        CanControl && (PauseInternet || DownloadLimit > 0 || UploadLimit > 0);

    public bool HasActiveRule => IsMonitoring && HasConfiguredRule;

    public string PolicyStateText => IsProtected
        ? "Protected"
        : !HasConfiguredRule
            ? "No pause/rate rule"
            : !IsMonitoring
                ? PauseInternet ? "IPv4 pause saved" : "IPv4 limits saved"
                : PauseInternet ? "IPv4 pause enabled" : "IPv4 limits enabled";

    public string PauseStateText => IsProtected
        ? "Unavailable — protected"
        : !PauseInternet
            ? "Not paused by this rule"
            : IsMonitoring ? "IPv4 pause enabled" : "IPv4 pause saved — control stopped";

    public string ProtectedReason
    {
        get => protectedReason;
        private set
        {
            if (SetField(ref protectedReason, value))
            {
                RaiseProtectionChanged();
            }
        }
    }

    public void Initialize(
        DeviceSnapshot snapshot,
        DevicePreferences? preferences,
        bool isProtectedDevice,
        string protectedDeviceReason)
    {
        MacKey = snapshot.MacAddress.ToString();
        MacAddress = FormatMac(MacKey);
        alias = string.IsNullOrWhiteSpace(preferences?.Alias) ? null : preferences.Alias.Trim();
        downloadLimit = Math.Max(0, preferences?.DownloadKiloBytesPerSecond ?? 0);
        uploadLimit = Math.Max(0, preferences?.UploadKiloBytesPerSecond ?? 0);
        pauseInternet = preferences?.PauseInternet ?? false;
        SetProtection(isProtectedDevice, protectedDeviceReason);
        OnPropertyChanged(nameof(Alias));
        OnPropertyChanged(nameof(EditableName));
        OnPropertyChanged(nameof(EditableAlias));
        OnPropertyChanged(nameof(DownloadLimit));
        OnPropertyChanged(nameof(UploadLimit));
        OnPropertyChanged(nameof(PauseInternet));
        RaiseRuleStateChanged();
        Update(snapshot, preferences?.Alias);
    }

    public void SetProtection(bool isProtectedDevice, string protectedDeviceReason)
    {
        ProtectedReason = isProtectedDevice ? protectedDeviceReason : string.Empty;
        IsProtected = isProtectedDevice;
    }

    public void Update(DeviceSnapshot snapshot, string? alias)
    {
        IpAddress = snapshot.IpAddress.Equals(IPAddress.Any) || snapshot.IpAddress.Equals(IPAddress.IPv6Any)
            ? "-"
            : snapshot.IpAddress.ToString();
        ObservedAt = snapshot.LastSeen;
        AutomaticName = string.IsNullOrWhiteSpace(snapshot.HostName)
            ? "Unknown device"
            : snapshot.HostName.Trim();
        // Saved aliases are loaded once; snapshot refreshes must not restore a just-cleared alias.
        DisplayName = this.alias ?? AutomaticName;
        ApplyRates(snapshot.DownloadBytesPerSecond, snapshot.UploadBytesPerSecond);
        UsageDownloadBytes = snapshot.UsageDownloadBytes;
        UsageUploadBytes = snapshot.UsageUploadBytes;
        UsageTotalBytes = snapshot.UsageTotalBytes;
    }

    public void SetPresence(bool online)
    {
        IsOnline = online;
    }

    public void SetMonitoring(bool monitoring)
    {
        if (!SetField(ref isMonitoring, monitoring, nameof(IsMonitoring)))
        {
            return;
        }

        ApplyRates(0, 0);
        OnPropertyChanged(nameof(PresenceText));
        OnPropertyChanged(nameof(MonitoringStateText));
        RaiseRuleStateChanged();
    }

    public void SetCaptureCoverage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        CaptureCoverageText = text;
    }

    public static string FormatRate(double bytesPerSecond)
    {
        return bytesPerSecond switch
        {
            >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / 1_000_000:0.0} MB/s"),
            >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond / 1_000:0.0} KB/s"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytesPerSecond:0} B/s"),
        };
    }

    public static string FormatBytes(long bytes)
    {
        double value = Math.Max(0, bytes);
        return value switch
        {
            >= 1_000_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000_000_000:0.00} TB"),
            >= 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000_000:0.00} GB"),
            >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000:0.0} MB"),
            >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000:0.0} KB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{value:0} B"),
        };
    }

    public static string FormatExactBytes(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, bytes):N0} bytes");

    private void ApplyRates(double download, double upload)
    {
        DownloadBytesPerSecond = IsMonitoring && double.IsFinite(download) ? Math.Max(0, download) : 0;
        UploadBytesPerSecond = IsMonitoring && double.IsFinite(upload) ? Math.Max(0, upload) : 0;
        TotalRate = DownloadBytesPerSecond + UploadBytesPerSecond;
        DownloadRate = IsMonitoring ? FormatRate(DownloadBytesPerSecond) : "—";
        UploadRate = IsMonitoring ? FormatRate(UploadBytesPerSecond) : "—";
    }

    private void RaiseProtectionChanged()
    {
        OnPropertyChanged(nameof(PauseInternet));
        OnPropertyChanged(nameof(CanControl));
        OnPropertyChanged(nameof(CanRename));
        OnPropertyChanged(nameof(IsLocalComputer));
        OnPropertyChanged(nameof(IsGateway));
        RaiseRuleStateChanged();
    }

    private void RaiseRuleStateChanged()
    {
        OnPropertyChanged(nameof(HasConfiguredRule));
        OnPropertyChanged(nameof(HasActiveRule));
        OnPropertyChanged(nameof(PolicyStateText));
        OnPropertyChanged(nameof(PauseStateText));
    }

    private static string FormatMac(string value) => value.Length == 12
        ? string.Join(":", Enumerable.Range(0, 6).Select(index => value.Substring(index * 2, 2)))
        : "-";

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
