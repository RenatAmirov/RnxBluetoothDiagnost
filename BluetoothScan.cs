using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace RnxBluetoothDiagnost;

internal sealed class BluetoothScan(DispatcherQueue dispatcher, Action<string> log) : IDisposable
{
    internal static readonly TimeSpan Duration = TimeSpan.FromSeconds(24);
    private readonly List<(DeviceWatcher Watcher, string Label)> watchers = [];
    private readonly ConcurrentDictionary<ulong, Advertisement> advertisements = new();
    private BluetoothLEAdvertisementWatcher? advertisementWatcher;
    private bool disposed;

    internal event Action<DeviceInformation, bool>? Added;
    internal event Action<DeviceInformationUpdate>? Updated;
    internal event Action<string>? Removed;
    internal bool AdvertisementsStarted { get; private set; }
    internal bool AdvertisementFailed { get; private set; }
    internal bool EndpointFailed { get; private set; }

    internal void Start(SystemCheck? system)
    {
        if (system?.SupportsClassic != false)
        {
            StartWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(false), false);
            StartWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true), false);
        }
        if (system?.SupportsLe != false)
        {
            StartWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(false), true);
            StartWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true), true);
            try
            {
                advertisementWatcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
                advertisementWatcher.Received += AdvertisementReceived;
                advertisementWatcher.Stopped += AdvertisementsStopped;
                advertisementWatcher.Start();
                AdvertisementsStarted = true;
            }
            catch (Exception error)
            {
                AdvertisementFailed = true;
                log($"BLE-пакеты: {BluetoothDiagnostics.Describe(error)}");
            }
        }
    }

    private void StartWatcher(string selector, bool isLe)
    {
        string label = isLe ? "BLE" : "Classic";
        try
        {
            var watcher = DeviceInformation.CreateWatcher(selector, BluetoothDiagnostics.EndpointProperties,
                DeviceInformationKind.AssociationEndpoint);
            watcher.Added += (_, info) => Dispatch(() => Added?.Invoke(info, isLe));
            watcher.Updated += (_, update) => Dispatch(() => Updated?.Invoke(update));
            watcher.Removed += (_, update) => Dispatch(() => Removed?.Invoke(update.Id));
            watcher.Stopped += (sender, _) =>
            {
                if (sender.Status == DeviceWatcherStatus.Aborted)
                    Dispatch(() =>
                    {
                        EndpointFailed = true;
                        log($"Поиск {label} через Windows прерван (Aborted).");
                    });
            };
            watchers.Add((watcher, label));
            watcher.Start();
        }
        catch (Exception error)
        {
            EndpointFailed = true;
            log($"Поиск {label} через Windows: {BluetoothDiagnostics.Describe(error)}");
        }
    }

    private void Dispatch(Action action) => dispatcher.TryEnqueue(() =>
    {
        if (!disposed)
            action();
    });

    private void AdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (args.BluetoothAddress == 0)
            return;

        string name = args.Advertisement.LocalName;
        int? rssi = args.RawSignalStrengthInDBm is > -127 and <= 20 ? args.RawSignalStrengthInDBm : null;
        advertisements.AddOrUpdate(args.BluetoothAddress,
            _ => new Advertisement(name, rssi, args.Timestamp),
            (_, previous) => new Advertisement(string.IsNullOrWhiteSpace(name) ? previous.Name : name, rssi, args.Timestamp));
    }

    private void AdvertisementsStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error != BluetoothError.Success)
            Dispatch(() =>
            {
                AdvertisementFailed = true;
                log($"Приём BLE-пакетов прерван ({args.Error}).");
            });
    }

    internal IEnumerable<(ulong Address, Advertisement Packet)> DrainAdvertisements()
    {
        foreach (ulong address in advertisements.Keys)
        {
            if (advertisements.TryRemove(address, out var packet))
                yield return (address, packet);
        }
    }

    internal static async Task<DeviceInformationCollection> FindPairedAsync(bool isLe)
    {
        string selector = isLe
            ? BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)
            : BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        return await DeviceInformation.FindAllAsync(selector, BluetoothDiagnostics.EndpointProperties, DeviceInformationKind.AssociationEndpoint);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        foreach (var (watcher, label) in watchers)
        {
            try
            {
                if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                    watcher.Stop();
            }
            catch (Exception error)
            {
                log($"Остановка поиска {label}: {BluetoothDiagnostics.Describe(error)}");
            }
        }
        watchers.Clear();

        if (advertisementWatcher is not null)
        {
            advertisementWatcher.Received -= AdvertisementReceived;
            advertisementWatcher.Stopped -= AdvertisementsStopped;
            try
            {
                if (advertisementWatcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                    advertisementWatcher.Stop();
            }
            catch (Exception error)
            {
                log($"Остановка приёма BLE-пакетов: {BluetoothDiagnostics.Describe(error)}");
            }
        }
    }
}
