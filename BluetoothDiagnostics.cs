using System.ComponentModel;
using System.ServiceProcess;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;
using Windows.Devices.Radios;
using Windows.Media.Devices;

namespace RnxBluetoothDiagnost;

internal static class BluetoothDiagnostics
{
    internal const string ContainerId = "System.Devices.ContainerId";
    internal const string AepContainerId = "System.Devices.Aep.ContainerId";
    internal const string DeviceAddress = "System.Devices.Aep.DeviceAddress";
    internal const string IsConnected = "System.Devices.Aep.IsConnected";
    internal const string IsPresent = "System.Devices.Aep.IsPresent";
    internal const string Category = "System.Devices.Aep.Category";
    private const string SetupClass = "{A45C254E-DF1C-4EFD-8020-67D146A850E0} 10";
    private const string DriverVersion = "{A8B865DD-2E3D-4094-AD97-E593A70C75D6} 3";
    private const string ProblemCode = "{4340A6C5-93FA-4706-972C-7B648008A5A7} 3";
    internal static readonly string[] EndpointProperties = [DeviceAddress, IsConnected, IsPresent, AepContainerId, ContainerId, Category];

    internal static async Task<SystemCheck> CheckSystemAsync()
    {
        var lines = new List<string>();
        bool? supportsClassic = null;
        bool? supportsLe = null;
        RadioState? state = null;
        string title = "Состояние Bluetooth неизвестно";
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            var radio = adapter is null ? null : await adapter.GetRadioAsync();
            radio ??= (await Radio.GetRadiosAsync()).FirstOrDefault(item => item.Kind == RadioKind.Bluetooth);
            state = radio?.State;
            title = state switch
            {
                RadioState.On => "Bluetooth включён",
                RadioState.Off => "Bluetooth выключен",
                RadioState.Disabled => "Bluetooth заблокирован",
                _ when adapter is null && radio is null => "Адаптер не найден",
                _ => "Состояние Bluetooth неизвестно"
            };
            if (adapter is not null)
            {
                supportsClassic = adapter.IsClassicSupported;
                supportsLe = adapter.IsLowEnergySupported;
                lines.Add($"Classic: {YesNo(supportsClassic.Value)} · BLE: {YesNo(supportsLe.Value)}");
            }
            if (radio is not null)
                lines.Add(radio.Name);
        }
        catch (Exception error)
        {
            lines.Add($"Радиомодуль: {Describe(error)}");
        }

        try
        {
            var bluetoothClass = new Guid("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
            var nodes = await PnpObject.FindAllAsync(PnpObjectType.Device,
                ["System.ItemNameDisplay", "System.Devices.Present", SetupClass, DriverVersion, ProblemCode],
                "System.Devices.Present:=System.StructuredQueryType.Boolean#True");
            var adapters = nodes.Where(node => ReadGuid(node.Properties, SetupClass) == bluetoothClass
                && !node.Id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)
                && !node.Id.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase));
            foreach (var node in adapters)
            {
                string name = Get(node.Properties, "System.ItemNameDisplay") as string ?? node.Id;
                string driver = Get(node.Properties, DriverVersion)?.ToString() ?? "не определён";
                var problem = Get(node.Properties, ProblemCode);
                lines.Add($"{name}\nДрайвер {driver}" + (problem is not null && problem.ToString() != "0" ? $" · код PnP {problem}" : ""));
            }
        }
        catch (Exception error)
        {
            lines.Add($"Драйвер: {Describe(error)}");
        }

        lines.AddRange(await Task.Run(() => new[] { ServiceState("bthserv"), ServiceState("BTAGService"), ServiceState("BthAvctpSvc") }));
        return new SystemCheck(title, string.Join(Environment.NewLine, lines), state, supportsClassic, supportsLe);
    }

    internal static async Task<string> CheckAudioAsync(DiscoveredDevice device)
    {
        var outputs = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector(), [ContainerId]);
        var matching = outputs.Where(output => device.ContainerId.HasValue
            && ReadGuid(output.Properties, ContainerId) == device.ContainerId).ToArray();
        if (matching.Length > 0)
        {
            var enabled = matching.Where(output => output.IsEnabled).ToArray();
            if (enabled.Length == 0)
                return "Windows создала аудиовыход, но он отключён. Проверьте настройки звука.";

            string? defaultOutput = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            string names = string.Join(", ", enabled.Select(output => output.Name).Distinct());
            return enabled.Any(output => output.Id == defaultOutput)
                ? $"Аудиовыход выбран в Windows: {names}."
                : $"Аудиовыход доступен: {names}. Выберите его в настройках звука.";
        }

        var nameMatches = outputs.Where(output => output.IsEnabled && !string.IsNullOrWhiteSpace(device.Name)
            && output.Name.Contains(device.Name, StringComparison.OrdinalIgnoreCase)).Select(output => output.Name).Distinct().ToArray();
        if (nameMatches.Length > 0)
            return $"Похожие по имени аудиовыходы: {string.Join(", ", nameMatches)}. Сверьте их в настройках звука.";

        return device.Paired || device.Connected
            ? "Аудиовыход для этого устройства пока не найден. Включите наушники и проверьте подключение в Windows."
            : "Аудиовыход для этого устройства пока не найден. Сначала выполните сопряжение.";
    }

    internal static object? Get(IReadOnlyDictionary<string, object> properties, string name) =>
        properties.TryGetValue(name, out var value) ? value : null;

    internal static Guid? ReadGuid(IReadOnlyDictionary<string, object> properties, string name)
    {
        var value = Get(properties, name);
        var guid = value is Guid parsed ? parsed : Guid.TryParse(value as string, out parsed) ? parsed : Guid.Empty;
        return guid == Guid.Empty ? null : guid;
    }

    internal static string Describe(Exception error) => error is Win32Exception win32
        ? $"Win32 {win32.NativeErrorCode}: {error.Message}"
        : $"0x{error.HResult:X8}: {error.Message}";

    private static string ServiceState(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            return $"{name}: {service.Status switch
            {
                ServiceControllerStatus.Running => "работает",
                ServiceControllerStatus.Stopped => "остановлена",
                _ => service.Status.ToString()
            }}";
        }
        catch (Exception error)
        {
            return $"{name}: {Describe(error)}";
        }
    }

    private static string YesNo(bool value) => value ? "да" : "нет";
}

internal sealed record SystemCheck(string Title, string Details, RadioState? RadioState, bool? SupportsClassic, bool? SupportsLe);
