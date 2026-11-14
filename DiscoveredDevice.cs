using System.ComponentModel;
using System.Globalization;
using Windows.Devices.Enumeration;

namespace RnxBluetoothDiagnost;

internal sealed class DiscoveredDevice(bool isLe, ulong? address)
{
    internal bool IsLe { get; } = isLe;
    internal ulong? Address { get; } = address;
    internal DeviceInformation? Endpoint { get; set; }
    internal ClassicBluetooth.Device? Classic { get; set; }
    internal Advertisement? Ble { get; set; }
    internal string? PairingName { get; set; }
    internal bool? PairingOverride { get; set; }

    internal string Name => FirstName(Endpoint?.Name, Classic?.Name, Ble?.Name, PairingName);
    internal string Label => Name.Length == 0 ? "Устройство без имени" : Name;
    internal string AddressText => Address is ulong value ? FormatAddress(value) : "адрес неизвестен";
    internal bool Paired => PairingOverride ?? (Endpoint?.Pairing.IsPaired ?? Classic?.Authenticated ?? false);
    internal bool Connected => Endpoint is not null
        ? BluetoothDiagnostics.Get(Endpoint.Properties, BluetoothDiagnostics.IsConnected) as bool? ?? Classic?.Connected ?? false
        : Classic?.Connected ?? false;
    internal bool CanPair => !Paired && (Endpoint?.Pairing.CanPair == true
        || (!IsLe && Address.HasValue && Classic is not null && VisibleInScan));
    internal bool VisibleInScan { get; set; }
    internal bool Present => Endpoint is not null && BluetoothDiagnostics.Get(Endpoint.Properties, BluetoothDiagnostics.IsPresent) is true;
    internal bool IsAudio => Classic?.IsAudioVideo == true
        || (Endpoint is not null && BluetoothDiagnostics.Get(Endpoint.Properties, BluetoothDiagnostics.Category) is string[] categories
            && categories.Any(category => category.Contains("Audio", StringComparison.OrdinalIgnoreCase)));

    internal Guid? ContainerId => Endpoint is null ? null
        : BluetoothDiagnostics.ReadGuid(Endpoint.Properties, BluetoothDiagnostics.ContainerId)
        ?? BluetoothDiagnostics.ReadGuid(Endpoint.Properties, BluetoothDiagnostics.AepContainerId);

    internal string Technology => IsLe ? "BLE" : "Classic";
    internal string State => Connected ? "подключено" : Paired ? "сопряжено" : CanPair ? "доступно для сопряжения"
        : Present || VisibleInScan || Ble is not null ? "обнаружено" : "запись Windows";

    internal string Details
    {
        get
        {
            var lines = new List<string> { $"Bluetooth {Technology}  ·  {AddressText}  ·  {State}" };
            if (Endpoint is not null)
                lines.Add($"Windows видит устройство сейчас: {(Present ? "да" : "нет или нет данных")}.");
            if (Classic is not null)
            {
                lines.Add($"Classic inquiry: {(VisibleInScan ? "ответ получен во время поиска" : "кэш Windows")}"
                    + (Classic.LastSeen is { } seen ? $" · последнее обнаружение {seen.ToLocalTime():dd.MM HH:mm:ss}" : "") + ".");
                if (Classic.Remembered && !Paired)
                    lines.Add("Windows сохранила сведения об устройстве. Сопряжение отсутствует.");
            }
            if (Ble is not null)
                lines.Add($"Последний пакет BLE получен в {Ble.Seen.ToLocalTime():HH:mm:ss}.");
            if (IsLe && Endpoint is null)
                lines.Add("Получен пакет BLE. Для сопряжения дождитесь записи Windows или выберите запись Classic этих наушников.");
            else if (!Paired && !CanPair)
                lines.Add("Переведите наушники в режим сопряжения и повторите поиск.");
            return string.Join(Environment.NewLine, lines);
        }
    }

    internal static ulong? ParseAddress(string? text)
    {
        if (text is null)
            return null;
        string hex = text.Replace(":", "").Replace("-", "").Trim();
        return hex.Length == 12 && ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong address)
            && address != 0 ? address : null;
    }

    private static string FormatAddress(ulong address)
    {
        string hex = address.ToString("X12", CultureInfo.InvariantCulture);
        return string.Join(":", Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2)));
    }

    private static string FirstName(params string?[] names) =>
        names.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))?.Trim() ?? "";
}

internal sealed record Advertisement(string Name, int? Rssi, DateTimeOffset Seen);

public sealed class DeviceRow : INotifyPropertyChanged
{
    internal DeviceRow(DiscoveredDevice device) => Device = device;

    internal DiscoveredDevice Device { get; }
    public string Title => Device.Label;
    public string Subtitle => $"{Device.Technology}  ·  {Device.AddressText}  ·  {Device.State}";
    public string Signal => Device.Ble?.Rssi is int value ? $"{value} дБм" : "";
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
