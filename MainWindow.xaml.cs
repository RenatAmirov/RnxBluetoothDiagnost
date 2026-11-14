using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.System;
using static RnxBluetoothDiagnost.BluetoothDiagnostics;

namespace RnxBluetoothDiagnost;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<(bool IsLe, ulong Address), DeviceRow> byAddress = [];
    private readonly Dictionary<string, DeviceRow> byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<DeviceRow> results = [];
    private readonly ObservableCollection<DeviceRow> visibleRows = [];
    private readonly Queue<string> journal = new();
    private SystemCheck? systemCheck;
    private DeviceRow? selected;
    private CancellationTokenSource? scanCancellation;
    private bool systemBusy;
    private bool operationBusy;
    private bool updatingSelection;
    private bool closed;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Bluetooth-диагностика";
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        scale = Math.Max(1, scale);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)(1140 * scale), (int)(800 * scale)));
        Devices.ItemsSource = visibleRows;
        Closed += (_, _) =>
        {
            closed = true;
            scanCancellation?.Cancel();
        };
        Root.Loaded += RootLoaded;
        UpdateInstructions();
        UpdateActions();
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private async void RootLoaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= RootLoaded;
        await CheckAsync();
    }

    private void LayoutChanged(object sender, SizeChangedEventArgs args)
    {
        bool stacked = args.NewSize.Width < 870;
        SideWidth.Width = new GridLength(stacked ? 0 : 306);
        Workspace.RowSpacing = stacked ? 16 : 0;
        Workspace.ColumnSpacing = stacked ? 0 : 18;
        Grid.SetColumnSpan(Sidebar, stacked ? 2 : 1);
        Grid.SetColumn(ResultsPane, stacked ? 0 : 1);
        Grid.SetColumnSpan(ResultsPane, stacked ? 2 : 1);
        Grid.SetRow(ResultsPane, stacked ? 1 : 0);
        DeviceActions.Orientation = args.NewSize.Width < 500 ? Orientation.Vertical : Orientation.Horizontal;
    }

    private void TargetChanged(object sender, SelectionChangedEventArgs args)
    {
        UpdateInstructions();
        if (ResultCount is not null && scanCancellation is null)
        {
            ScanStatus.Text = "Переведите выбранную модель в режим сопряжения и запустите поиск.";
            RefreshList();
        }
    }

    private void UpdateInstructions()
    {
        if (Instructions is null)
            return;

        Instructions.Text = Target.SelectedIndex switch
        {
            0 => "На G435 удерживайте Power + Mute не менее 3 секунд. Синий индикатор должен мигать. Если гарнитура в LIGHTSPEED, удерживайте Mute 3 секунды для переключения в Bluetooth.",
            1 => "W820NB Plus: на выключенных наушниках удерживайте кнопку питания около 6 секунд до частого сине-зелёного мигания. Если на корпусе есть кнопка Bluetooth, на новой ревизии удерживайте её около 3 секунд при включённых наушниках.",
            _ => "Зарядите наушники и включите режим сопряжения по инструкции вашей модели."
        };
    }

    private async void CheckClick(object sender, RoutedEventArgs args) => await CheckAsync();

    private async Task CheckAsync()
    {
        if (systemBusy || closed)
            return;

        systemBusy = true;
        UpdateActions();
        RadioTitle.Text = "Проверка Bluetooth…";
        try
        {
            systemCheck = await CheckSystemAsync();
            if (closed)
                return;

            RadioTitle.Text = systemCheck.Title;
            SystemDetails.Text = systemCheck.Details;
            Log($"Система: {systemCheck.Title}. {systemCheck.Details.Replace(Environment.NewLine, " | ")}");
            await LoadPairedAsync(reconcile: true);
        }
        catch (Exception error)
        {
            if (!closed)
            {
                RadioTitle.Text = "Ошибка диагностики";
                SystemDetails.Text = Describe(error);
                Log($"Система: {Describe(error)}");
            }
        }
        finally
        {
            systemBusy = false;
            if (!closed)
                UpdateActions();
        }
    }

    private async Task LoadPairedAsync(bool reconcile = false)
    {
        foreach (bool isLe in new[] { false, true })
        {
            try
            {
                var paired = await BluetoothScan.FindPairedAsync(isLe);
                if (closed)
                    return;

                if (reconcile)
                {
                    var currentIds = paired.Select(endpoint => endpoint.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var stale = results.Where(row => row.Device.IsLe == isLe && row.Device.Endpoint?.Pairing.IsPaired == true
                            && !currentIds.Contains(row.Device.Endpoint.Id))
                        .ToArray();
                    foreach (var row in stale)
                    {
                        row.Device.PairingOverride = false;
                        RemoveEndpoint(row.Device.Endpoint!.Id);
                    }
                }

                foreach (var endpoint in paired)
                {
                    ApplyEndpoint(endpoint, isLe);
                }
            }
            catch (Exception error)
            {
                Log($"Список сопряжённых {(isLe ? "BLE" : "Classic")}: {Describe(error)}");
            }
        }
        if (!closed)
            RefreshList();
    }

    private async void ScanClick(object sender, RoutedEventArgs args)
    {
        if (scanCancellation is not null)
        {
            scanCancellation.Cancel();
            ScanLabel.Text = "Остановка…";
            UpdateActions();
            return;
        }

        await ScanAsync();
    }

    private async Task ScanAsync()
    {
        using var cancellation = new CancellationTokenSource();
        scanCancellation = cancellation;
        ScanProgress.Visibility = Visibility.Visible;
        ScanLabel.Text = "Остановить";
        ScanIcon.Symbol = Symbol.Stop;
        UpdateActions();
        SetOperation(null);
        ScanStatus.Text = "Проверяем адаптер перед поиском…";
        try
        {
            await CheckAsync();
            if (closed)
                return;
            if (cancellation.IsCancellationRequested)
            {
                ScanStatus.Text = "Поиск остановлен.";
                return;
            }

            if (systemCheck?.RadioState is RadioState.Off or RadioState.Disabled)
            {
                ScanStatus.Text = $"{systemCheck.Title}. Проверьте Bluetooth и режим «В самолёте» в Windows.";
                return;
            }

            ResetResults();
            ScanStatus.Text = "Идёт поиск до 24 секунд. Держите наушники рядом с ноутбуком.";
            Log("Начат поиск Bluetooth Classic и BLE.");

            using var scan = new BluetoothScan(DispatcherQueue, Log);
            scan.Added += ApplyEndpoint;
            scan.Updated += UpdateEndpoint;
            scan.Removed += RemoveEndpoint;
            scan.Start(systemCheck);
            DateTimeOffset started = DateTimeOffset.UtcNow;
            var elapsed = Stopwatch.StartNew();
            Task<IReadOnlyList<ClassicBluetooth.Device>>? classic = systemCheck?.SupportsClassic != false
                ? Task.Run(ClassicBluetooth.FindNearby) : null;
            bool classicError = false;

            async Task CompleteClassicInquiryAsync()
            {
                if (classic is null)
                    return;
                var task = classic;
                classic = null;
                try
                {
                    var devices = await task;
                    if (closed)
                        return;
                    foreach (var device in devices)
                    {
                        if (device.Address == 0)
                            continue;
                        var row = GetRow(false, device.Address, null);
                        row.Device.Classic = device;
                        row.Device.VisibleInScan = device.LastSeen >= started.AddSeconds(-1)
                            && device.LastSeen <= DateTimeOffset.UtcNow.AddSeconds(1);
                    }
                    Log($"Classic inquiry: {devices.Count} записей, включая кэш Windows.");
                }
                catch (Exception error)
                {
                    classicError = true;
                    Log($"Classic inquiry: {Describe(error)}");
                }
            }

            try
            {
                while (elapsed.Elapsed < BluetoothScan.Duration)
                {
                    await Task.Delay(400, cancellation.Token);
                    if (classic?.IsCompleted == true)
                        await CompleteClassicInquiryAsync();
                    ApplyAdvertisements(scan);
                    RefreshList();
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                scan.Dispose();
                if (!closed && classic is not null)
                    ScanStatus.Text = "Завершается запрос Bluetooth Classic…";
            }

            await CompleteClassicInquiryAsync();
            if (closed)
                return;

            ApplyAdvertisements(scan);
            RefreshList();
            SummarizeScan(cancellation.IsCancellationRequested, classicError || scan.EndpointFailed,
                scan.AdvertisementsStarted && !scan.AdvertisementFailed);
        }
        catch (Exception error)
        {
            if (!closed)
            {
                ScanStatus.Text = $"Ошибка поиска. {Describe(error)}";
                Log(ScanStatus.Text);
            }
        }
        finally
        {
            scanCancellation = null;
            if (!closed)
            {
                ScanProgress.Visibility = Visibility.Collapsed;
                ScanIcon.Symbol = Symbol.Find;
                ScanLabel.Text = "Начать поиск";
                UpdateActions();
            }
        }
    }

    private void ResetResults()
    {
        string? selectedId = selected?.Device.Endpoint?.Id;
        var paired = results.Where(row => row.Device.Paired && row.Device.Endpoint is not null)
            .Select(row => (Endpoint: row.Device.Endpoint!, row.Device.IsLe)).ToArray();
        results.Clear();
        byAddress.Clear();
        byId.Clear();
        selected = null;
        foreach (var (endpoint, isLe) in paired)
        {
            ApplyEndpoint(endpoint, isLe);
            if (endpoint.Id == selectedId)
                selected = byId[endpoint.Id];
        }
        RefreshList();
    }

    private void ApplyAdvertisements(BluetoothScan scan)
    {
        foreach (var (address, packet) in scan.DrainAdvertisements())
        {
            var row = GetRow(true, address, null);
            var previous = row.Device.Ble;
            if (previous is not null && previous.Seen > packet.Seen)
                continue;

            row.Device.Ble = string.IsNullOrWhiteSpace(packet.Name) && previous is not null
                ? packet with { Name = previous.Name } : packet;
        }
    }

    private void ApplyEndpoint(DeviceInformation endpoint, bool isLe)
    {
        ulong? address = EndpointAddress(endpoint);
        var row = GetRow(isLe, address, endpoint.Id);
        row.Device.Endpoint = endpoint;
        row.Device.PairingOverride = null;
        row.Refresh();
    }

    private static ulong? EndpointAddress(DeviceInformation endpoint)
    {
        var property = DiscoveredDevice.ParseAddress(Get(endpoint.Properties, DeviceAddress) as string);
        if (property.HasValue)
            return property;

        int dash = endpoint.Id.LastIndexOf('-');
        return dash >= 0 ? DiscoveredDevice.ParseAddress(endpoint.Id[(dash + 1)..]) : null;
    }

    private void UpdateEndpoint(DeviceInformationUpdate update)
    {
        if (!byId.TryGetValue(update.Id, out var row) || row.Device.Endpoint is not { } endpoint)
            return;

        endpoint.Update(update);
        ApplyEndpoint(endpoint, row.Device.IsLe);
    }

    private void RemoveEndpoint(string id)
    {
        if (!byId.Remove(id, out var row) || !string.Equals(row.Device.Endpoint?.Id, id, StringComparison.OrdinalIgnoreCase))
            return;

        row.Device.Endpoint = null;
        if (row.Device.Classic is null && row.Device.Ble is null && row.Device.PairingOverride != true)
        {
            results.Remove(row);
            if (row.Device.Address is ulong address)
                byAddress.Remove((row.Device.IsLe, address));
        }
    }

    private DeviceRow GetRow(bool isLe, ulong? address, string? id)
    {
        DeviceRow? addressRow = address is ulong value ? byAddress.GetValueOrDefault((isLe, value)) : null;
        DeviceRow? idRow = id is not null ? byId.GetValueOrDefault(id) : null;
        if (idRow is not null && address is null)
            return idRow;

        var row = addressRow ?? new DeviceRow(new DiscoveredDevice(isLe, address));
        results.Add(row);
        if (address is ulong key)
            byAddress[(isLe, key)] = row;
        if (id is not null)
            byId[id] = row;
        if (idRow is not null && idRow != row)
        {
            results.Remove(idRow);
            if (idRow.Device.Address is ulong oldAddress)
                byAddress.Remove((idRow.Device.IsLe, oldAddress));
            if (selected == idRow)
                selected = row;
        }
        return row;
    }

    private void FilterChanged(object sender, TextChangedEventArgs args)
    {
        if (Devices is not null && EmptyList is not null && ResultCount is not null)
            RefreshList();
    }

    private void RefreshList()
    {
        string filter = Filter.Text.Trim();
        ulong? filterAddress = DiscoveredDevice.ParseAddress(filter);
        var desired = results.Where(row => filter.Length == 0
                || row.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || row.Device.AddressText.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (filterAddress.HasValue && row.Device.Address == filterAddress))
            .OrderByDescending(row => IsTarget(row.Device) || row.Device.IsAudio)
            .ThenBy(row => row.Device.IsLe)
            .ThenBy(row => row.Device.Name.Length == 0)
            .ThenBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.Device.AddressText, StringComparer.Ordinal)
            .ToArray();
        var visible = desired.ToHashSet();
        updatingSelection = true;
        try
        {
            for (int index = visibleRows.Count - 1; index >= 0; index--)
                if (!visible.Contains(visibleRows[index]))
                    visibleRows.RemoveAt(index);
            for (int index = 0; index < desired.Length; index++)
            {
                var row = desired[index];
                row.Refresh();
                if (index < visibleRows.Count && visibleRows[index] == row)
                    continue;
                int previous = visibleRows.IndexOf(row);
                if (previous >= 0)
                    visibleRows.Move(previous, index);
                else
                    visibleRows.Insert(index, row);
            }

            if (selected is not null && !visible.Contains(selected) && !operationBusy)
            {
                selected = null;
                SetOperation(null);
            }
            Devices.SelectedItem = selected is not null && visible.Contains(selected) ? selected : null;
        }
        finally
        {
            updatingSelection = false;
        }

        EmptyList.Text = filter.Length == 0 ? "Устройства появятся здесь" : "Совпадений нет";
        EmptyList.Visibility = desired.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultCount.Text = $"Показано {desired.Length} из {results.Count}  ·  Classic {results.Count(row => !row.Device.IsLe)}  ·  BLE {results.Count(row => row.Device.IsLe)}";
        RefreshSelection();
    }

    private void DeviceSelected(object sender, SelectionChangedEventArgs args)
    {
        if (updatingSelection)
            return;

        selected = Devices.SelectedItem as DeviceRow;
        SetOperation(null);
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        var device = selected?.Device;
        SelectedTitle.Text = device?.Label ?? "Сопряжение и звук";
        SelectedDetails.Text = device?.Details ?? "Выберите устройство в списке.";
        if (device?.Ble is { Rssi: int signal } packet)
        {
            SignalPanel.Visibility = Visibility.Visible;
            SignalMeter.Value = Math.Clamp(signal, -100, -30);
            SignalReading.Text = $"BLE {signal} дБм · {packet.Seen.ToLocalTime():HH:mm:ss}";
        }
        else
        {
            SignalPanel.Visibility = Visibility.Collapsed;
        }
        UpdateActions();
    }

    private void UpdateActions()
    {
        bool scanning = scanCancellation is not null;
        ScanButton.IsEnabled = (!systemBusy || scanning) && !operationBusy && scanCancellation?.IsCancellationRequested != true;
        CheckButton.IsEnabled = !scanning && !systemBusy && !operationBusy;
        Target.IsEnabled = !scanning;
        Devices.IsEnabled = !operationBusy;
        Filter.IsEnabled = !operationBusy;
        var device = selected?.Device;
        bool available = !systemBusy && !operationBusy;
        PairButton.IsEnabled = available && device?.CanPair == true;
        UnpairButton.IsEnabled = available && device?.Paired == true && (device.Endpoint is not null || !device.IsLe && device.Address.HasValue);
        AudioButton.IsEnabled = available && device is not null;
    }

    private bool IsTarget(DiscoveredDevice device) => Target.SelectedIndex switch
    {
        0 => device.Name.Contains("G435", StringComparison.OrdinalIgnoreCase),
        1 => device.Name.Contains("EDIFIER", StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains("W820NB", StringComparison.OrdinalIgnoreCase),
        _ => device.IsAudio
    };

    private void SummarizeScan(bool canceled, bool discoveryError, bool bleRan)
    {
        string prefix = canceled ? "Поиск остановлен. " : "";
        var target = results.Select(row => row.Device).Where(IsTarget).ToArray();
        int liveClassic = results.Count(row => !row.Device.IsLe && IsLive(row.Device));
        int blePackets = results.Count(row => row.Device.Ble is not null);
        string targetName = (Target.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "наушники";
        ScanStatus.Text = prefix + (target.Any(device => !device.IsLe && IsLive(device))
            ? $"{targetName}: обнаружена запись Bluetooth Classic. Выберите её для сопряжения или проверки звука."
            : target.Any(device => device.IsLe && IsLive(device))
                ? $"{targetName}: есть отклик BLE. Проверьте состояние записи. Пакеты BLE сами по себе не подтверждают доступность аудио."
                : target.Length > 0
                    ? $"{targetName}: найдена сохранённая запись без свежего отклика. Проверьте режим сопряжения и второй ноутбук."
                    : liveClassic > 0
                        ? $"{targetName}: отклика нет, другие устройства Classic обнаружены. Проверьте режим сопряжения гарнитуры и второй ноутбук."
                        : $"{targetName}: отклика нет. Проверьте заряд и режим сопряжения, затем повторите поиск рядом с ноутбуком.");
        string summary = $"Classic: свежих записей {liveClassic}. BLE: пакеты от {blePackets} адресов."
            + (bleRan ? "" : " Сканер BLE не отработал полностью.")
            + (discoveryError ? " Есть ошибки поиска. Подробности в журнале." : "");
        ScanStatus.Text += $" {summary}";
        Log(ScanStatus.Text);
    }

    private static bool IsLive(DiscoveredDevice device) =>
        device.Present || device.VisibleInScan || device.Connected || device.Ble is not null;

    private async void PairClick(object sender, RoutedEventArgs args)
    {
        await RunSelectedAsync("Сопряжение", async device =>
        {
            if (!device.CanPair)
                return "Переведите наушники в режим сопряжения и повторите поиск.";

            bool paired;
            string message;
            if (device.Endpoint?.Pairing.CanPair == true)
            {
                var result = await device.Endpoint.Pairing.PairAsync();
                paired = result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired;
                message = PairingMessage(result.Status);
            }
            else if (!device.IsLe && device.Address is ulong address)
            {
                IntPtr window = WinRT.Interop.WindowNative.GetWindowHandle(this);
                (paired, message) = await Task.Run(() => ClassicBluetooth.Pair(window, address));
            }
            else
            {
                return "Windows пока не предоставила запись для сопряжения.";
            }

            if (!paired || closed)
                return message;

            device.PairingName = device.Name;
            device.PairingOverride = true;
            await LoadPairedAsync();
            await Task.Delay(1000);
            if (closed)
                return message;
            try
            {
                return $"{message}\n{await CheckAudioAsync(selected?.Device ?? device)}";
            }
            catch (Exception error)
            {
                return $"{message}\nПроверка аудиовыхода: {Describe(error)}";
            }
        }, "Подтвердите сопряжение в диалоге Windows, если он появится.");
    }

    private async void UnpairClick(object sender, RoutedEventArgs args)
    {
        await RunSelectedAsync("Удаление сопряжения", async device =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Удалить сопряжение?",
                Content = $"Windows забудет «{device.Label}» на этом ноутбуке.",
                PrimaryButtonText = "Удалить",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || closed)
                return "Удаление отменено.";

            if (device.Endpoint?.Pairing.IsPaired == true)
            {
                var result = await device.Endpoint.Pairing.UnpairAsync();
                if (result.Status is not (DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired))
                    return $"Windows не удалила сопряжение ({result.Status}).";
            }
            else if (!device.IsLe && device.Address is ulong address)
            {
                await Task.Run(() => ClassicBluetooth.Forget(address));
            }
            else
            {
                return "Обновите список устройств перед удалением сопряжения.";
            }

            device.PairingOverride = false;
            device.PairingName = device.Name;
            if (device.Endpoint is { } endpoint)
                RemoveEndpoint(endpoint.Id);
            if (device.Classic is { } classic)
                device.Classic = classic with { Authenticated = false, Remembered = false, Connected = false };
            return "Сопряжение удалено. Снова включите режим сопряжения на наушниках и запустите поиск.";
        });
    }

    private async void AudioClick(object sender, RoutedEventArgs args) =>
        await RunSelectedAsync("Проверка аудиовыхода", CheckAudioAsync);

    private async Task RunSelectedAsync(string title, Func<DiscoveredDevice, Task<string>> operation, string? pending = null)
    {
        if (selected is not { } row || operationBusy || systemBusy || closed)
            return;

        operationBusy = true;
        UpdateActions();
        SetOperation(pending ?? $"{title}…");
        Log($"{title}: {row.Title} ({row.Device.Technology}, {row.Device.AddressText}).");
        try
        {
            string message = await operation(row.Device);
            if (!closed)
            {
                SetOperation(message);
                Log(message.Replace("\n", " | "));
            }
        }
        catch (Exception error)
        {
            if (!closed)
            {
                SetOperation($"{title}: {Describe(error)}");
                Log(OperationStatus.Text);
            }
        }
        finally
        {
            operationBusy = false;
            if (!closed)
            {
                if (selected is not null && !results.Contains(selected))
                    selected = null;
                RefreshList();
            }
        }
    }

    private static string PairingMessage(DevicePairingResultStatus status)
    {
        string message = status switch
        {
            DevicePairingResultStatus.Paired => "Сопряжение выполнено.",
            DevicePairingResultStatus.AlreadyPaired => "Windows уже хранит сопряжение.",
            DevicePairingResultStatus.NotReadyToPair or DevicePairingResultStatus.ConnectionRejected
                or DevicePairingResultStatus.AuthenticationTimeout => "Наушники не завершили сопряжение. Проверьте индикатор и отключите Bluetooth на втором ноутбуке.",
            DevicePairingResultStatus.AuthenticationFailure or DevicePairingResultStatus.RemoteDeviceHasAssociation =>
                "Ошибка проверки сопряжения. Проверьте сохранённые связи и заново включите режим сопряжения на наушниках.",
            DevicePairingResultStatus.PairingCanceled => "Сопряжение отменено.",
            DevicePairingResultStatus.AccessDenied => "Windows отклонила доступ к сопряжению. Проверьте разрешения и попробуйте через настройки Bluetooth.",
            DevicePairingResultStatus.NoSupportedProfiles => "Windows не нашла подходящий профиль. Если есть запись Classic, выберите её.",
            DevicePairingResultStatus.OperationAlreadyInProgress => "Дождитесь окончания другой операции сопряжения Windows.",
            _ => "Windows не завершила сопряжение."
        };
        return $"{message} ({status})";
    }

    private void SetOperation(string? message)
    {
        OperationStatus.Text = message ?? "";
        OperationStatus.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Log(string message)
    {
        if (closed)
            return;

        if (journal.Count == 160)
            journal.Dequeue();
        journal.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
        JournalText.Text = string.Join(Environment.NewLine, journal);
    }

    private void CopyClick(object sender, RoutedEventArgs args)
    {
        var report = new StringBuilder();
        report.AppendLine($"Bluetooth-диагностика · {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} · Windows {Environment.OSVersion.Version}");
        report.AppendLine(RadioTitle.Text).AppendLine(SystemDetails.Text).AppendLine(ScanStatus.Text).AppendLine();
        foreach (var row in results.OrderBy(row => row.Device.IsLe).ThenBy(row => row.Title))
            report.AppendLine($"{row.Title} | {row.Subtitle} | {row.Signal} | {row.Device.Details.Replace(Environment.NewLine, " | ")}");
        report.AppendLine().AppendLine(JournalText.Text);
        try
        {
            var data = new DataPackage();
            data.SetText(report.ToString());
            Clipboard.SetContent(data);
            Clipboard.Flush();
            CopyStatus.Text = "Отчёт скопирован.";
        }
        catch (Exception error)
        {
            CopyStatus.Text = Describe(error);
        }
    }

    private async void OpenBluetoothClick(object sender, RoutedEventArgs args) => await OpenSettingsAsync("ms-settings:bluetooth");
    private async void OpenSoundClick(object sender, RoutedEventArgs args) => await OpenSettingsAsync("ms-settings:sound");

    private async Task OpenSettingsAsync(string uri)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri(uri)) && !closed)
                Log("Windows не открыла страницу параметров.");
        }
        catch (Exception error)
        {
            Log(Describe(error));
        }
    }
}
