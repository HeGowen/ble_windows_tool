using System.Reflection;
using System.ServiceProcess;
using System.Text.Json;
using BleWindowsTool.Ble;
using BleWindowsTool.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace BleWindowsTool.Diagnostics;

public sealed class BluetoothHealthSnapshot
{
    public required string Timestamp { get; init; }
    public required string OsVersion { get; init; }
    public required string ProcessArch { get; init; }
    public required string Framework { get; init; }
    public required List<string> ServiceStates { get; init; }
    public required List<string> RadioStates { get; init; }
    public required List<string> KnownDevices { get; init; }
    public required List<string> Notes { get; init; }
}

public static class BluetoothHealthReporter
{
    public static async Task<BluetoothHealthSnapshot> CollectAsync(DiagLogger logger, string outputJsonPath)
    {
        var serviceStates = GetServiceStates();
        var radioStates = await GetRadioStatesAsync();
        var knownDevices = await GetKnownDevicesAsync();
        var notes = await BuildNotesAsync();

        var snapshot = new BluetoothHealthSnapshot
        {
            Timestamp = DateTimeOffset.Now.ToString("O"),
            OsVersion = Environment.OSVersion.VersionString,
            ProcessArch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Framework = Assembly.GetEntryAssembly()?.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName ?? "unknown",
            ServiceStates = serviceStates,
            RadioStates = radioStates,
            KnownDevices = knownDevices,
            Notes = notes
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputJsonPath)!);
        await File.WriteAllTextAsync(outputJsonPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));

        logger.Info("bluetooth health snapshot:");
        foreach (var line in serviceStates)
        {
            logger.Info($"health service {line}");
        }

        foreach (var line in radioStates)
        {
            logger.Info($"health radio {line}");
        }

        foreach (var line in notes)
        {
            logger.Info($"health note {line}");
        }

        return snapshot;
    }

    private static List<string> GetServiceStates()
    {
        var list = new List<string>();
        try
        {
            var services = ServiceController.GetServices();
            var expectedPrefixes = new[] { "bthserv", "BluetoothUserService", "BTAGService" };

            foreach (var svc in services
                         .Where(s => expectedPrefixes.Any(p => s.ServiceName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                         .OrderBy(s => s.ServiceName))
            {
                list.Add($"{svc.ServiceName}:{svc.Status}");
            }

            if (list.Count == 0)
            {
                list.Add("no bluetooth-related services found via ServiceController");
            }
        }
        catch (Exception ex)
        {
            list.Add($"service_query_error={ex.GetType().Name}:{ex.Message}");
        }

        return list;
    }

    private static async Task<List<string>> GetRadioStatesAsync()
    {
        var list = new List<string>();
        try
        {
            var radios = await Radio.GetRadiosAsync();
            foreach (var radio in radios.Where(r => r.Kind == RadioKind.Bluetooth))
            {
                list.Add($"{radio.Name}:{radio.State}");
            }

            if (list.Count == 0)
            {
                list.Add("no bluetooth radios found");
            }
        }
        catch (Exception ex)
        {
            list.Add($"radio_query_error={ex.GetType().Name}:{ex.Message}");
        }

        return list;
    }

    private static async Task<List<string>> GetKnownDevicesAsync()
    {
        var list = new List<string>();
        try
        {
            var selector = BluetoothLEDevice.GetDeviceSelector();
            var infos = await DeviceInformation.FindAllAsync(selector);
            foreach (var info in infos.Take(20))
            {
                var addr = ReadProp(info.Properties, "System.Devices.Aep.DeviceAddress");
                var isConnected = ReadProp(info.Properties, "System.Devices.Aep.IsConnected");
                var paired = info.Pairing?.IsPaired == true;
                list.Add($"{(string.IsNullOrWhiteSpace(info.Name) ? "Unknown" : info.Name)}@{addr} paired={paired} connected={isConnected}");
            }

            if (infos.Count == 0)
            {
                list.Add("none");
            }
        }
        catch (Exception ex)
        {
            list.Add($"known_device_query_error={ex.GetType().Name}:{ex.Message}");
        }

        return list;
    }

    private static async Task<List<string>> BuildNotesAsync()
    {
        var notes = new List<string>();

        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null)
            {
                notes.Add("adapter=null (Windows BLE API returned no default adapter)");
            }
            else
            {
                notes.Add($"adapter_ok low_energy={adapter.IsLowEnergySupported} central_role={adapter.IsCentralRoleSupported}");
            }
        }
        catch (Exception ex)
        {
            notes.Add($"adapter_error={ex.GetType().Name}:{ex.Message}");
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var report = await BleScanner.ScanWithReportAsync(TimeSpan.FromSeconds(2), cts.Token);
            notes.Add($"quick_scan devices={report.Devices.Count} diag={report.Diagnostics}");
        }
        catch (Exception ex)
        {
            notes.Add($"quick_scan_error={ex.GetType().Name}:{ex.Message}");
        }

        return notes;
    }

    private static string ReadProp(IReadOnlyDictionary<string, object> props, string key)
    {
        if (props.TryGetValue(key, out var v) && v != null)
        {
            return v.ToString() ?? string.Empty;
        }

        return string.Empty;
    }
}
