using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Lantern.Core.Networking;

namespace Lantern.App.ViewModels;

public sealed class DeviceActivityViewModel : INotifyPropertyChanged
{
    private readonly HashSet<DomainObservationSource> sources = [];
    private string deviceName;
    private string ipAddress;
    private DateTimeOffset firstSeen;
    private DateTimeOffset lastSeen;
    private int observationCount;
    private bool isBlocked;
    private bool controlAvailable = true;
    private bool lastObservedDrop;

    public DeviceActivityViewModel(
        string macKey,
        string deviceName,
        string ipAddress,
        string domain,
        DomainObservationSource source,
        DateTimeOffset observedAt,
        bool lastObservedDrop = false)
    {
        MacKey = macKey;
        this.deviceName = deviceName;
        this.ipAddress = ipAddress;
        Domain = domain;
        sources.Add(source);
        firstSeen = observedAt;
        lastSeen = observedAt;
        observationCount = 1;
        this.lastObservedDrop = lastObservedDrop;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string MacKey { get; }

    public string DeviceName
    {
        get => deviceName;
        private set => SetField(ref deviceName, value);
    }

    public string IpAddress
    {
        get => ipAddress;
        private set => SetField(ref ipAddress, value);
    }

    public string Domain { get; }

    public string SourceLabel => string.Join(
        " + ",
        sources.OrderBy(source => source).Select(source => source.ToString().ToUpperInvariant()));

    public DateTimeOffset FirstSeen
    {
        get => firstSeen;
        private set
        {
            if (SetField(ref firstSeen, value))
            {
                OnPropertyChanged(nameof(FirstSeenText));
            }
        }
    }

    public string FirstSeenText => FirstSeen.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public DateTimeOffset LastSeen
    {
        get => lastSeen;
        private set
        {
            if (SetField(ref lastSeen, value))
            {
                OnPropertyChanged(nameof(LastSeenText));
            }
        }
    }

    public string LastSeenText => LastSeen.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public int ObservationCount
    {
        get => observationCount;
        private set
        {
            if (SetField(ref observationCount, value))
            {
                OnPropertyChanged(nameof(HitCount));
            }
        }
    }

    public int HitCount => ObservationCount;

    public bool IsBlocked
    {
        get => isBlocked;
        private set
        {
            if (SetField(ref isBlocked, value))
            {
                OnPropertyChanged(nameof(CanBlock));
                OnPropertyChanged(nameof(BlockActionText));
                OnPropertyChanged(nameof(CurrentRuleText));
            }
        }
    }

    public bool IsControlAvailable => controlAvailable;

    public bool CanBlock => IsControlAvailable && !IsBlocked;

    public string BlockActionText => !IsControlAvailable
        ? "Protected"
        : IsBlocked ? "Rule exists" : "Block domain";

    public string CurrentRuleText => IsBlocked ? "Block rule configured" : "No block rule";

    public bool LastObservedDrop
    {
        get => lastObservedDrop;
        private set
        {
            if (SetField(ref lastObservedDrop, value))
            {
                OnPropertyChanged(nameof(LastObservedDropText));
            }
        }
    }

    public string LastObservedDropText => LastObservedDrop
        ? "Last signal: dropped"
        : "Last signal: not dropped";

    public void SetBlocked(bool blocked)
    {
        IsBlocked = blocked;
    }

    public void SetControlAvailable(bool available)
    {
        if (SetField(ref controlAvailable, available, nameof(IsControlAvailable)))
        {
            OnPropertyChanged(nameof(CanBlock));
            OnPropertyChanged(nameof(BlockActionText));
        }
    }

    public void Observe(
        string latestDeviceName,
        string latestIpAddress,
        DomainObservationSource source,
        DateTimeOffset observedAt,
        bool lastObservedDrop = false)
    {
        ObserveBatch(
            latestDeviceName,
            latestIpAddress,
            [source],
            observedAt,
            observedAt,
            1,
            lastObservedDrop);
    }

    public void ObserveBatch(
        string latestDeviceName,
        string latestIpAddress,
        IEnumerable<DomainObservationSource> observationSources,
        DateTimeOffset firstObservedAt,
        DateTimeOffset lastObservedAt,
        int observationCount,
        bool lastObservedDrop = false)
    {
        ArgumentNullException.ThrowIfNull(observationSources);
        ArgumentOutOfRangeException.ThrowIfNegative(observationCount);
        DeviceName = latestDeviceName;
        IpAddress = latestIpAddress;
        var sourcesChanged = false;
        foreach (var source in observationSources)
        {
            sourcesChanged |= sources.Add(source);
        }

        if (sourcesChanged)
        {
            OnPropertyChanged(nameof(SourceLabel));
        }

        if (firstObservedAt < FirstSeen)
        {
            FirstSeen = firstObservedAt;
        }

        if (lastObservedAt >= LastSeen)
        {
            LastSeen = lastObservedAt;
            LastObservedDrop = lastObservedDrop;
        }

        ObservationCount = (int)Math.Min(int.MaxValue, (long)ObservationCount + observationCount);
    }

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
