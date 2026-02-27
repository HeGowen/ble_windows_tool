using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace BleWindowsTool.Ble;

public sealed class BleScanDevice
{
    public required ulong BluetoothAddress { get; init; }
    public required string Address { get; init; }
    public required string Name { get; init; }
    public required int Rssi { get; init; }
    public required Dictionary<ushort, byte[]> ManufacturerData { get; init; }
    public required List<Guid> ServiceUuids { get; init; }
}

public sealed class BleScanReport
{
    public required List<BleScanDevice> Devices { get; init; }
    public required string Diagnostics { get; init; }
}

public static class BleScanner
{
    public static async Task<BleScanReport> ScanWithReportAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();

        await AppendEnvironmentDiagnosticsAsync(diagnostics);

        var active = await ScanByModeAsync(BluetoothLEScanningMode.Active, timeout, cancellationToken);
        diagnostics.Add(active.DiagnosticLine);

        var map = new Dictionary<ulong, BleScanDevice>();
        MergeInto(map, active.Devices);
        var radioUnavailable = IsRadioUnavailableDiagnostic(active.DiagnosticLine);

        if (map.Count == 0 && !radioUnavailable)
        {
            var passive = await ScanByModeAsync(BluetoothLEScanningMode.Passive, timeout, cancellationToken);
            diagnostics.Add(passive.DiagnosticLine);
            MergeInto(map, passive.Devices);
            if (IsRadioUnavailableDiagnostic(passive.DiagnosticLine))
            {
                radioUnavailable = true;
            }
        }
        else if (radioUnavailable)
        {
            diagnostics.Add("scan_abort=radio_not_available");
        }

        if (map.Count == 0 && !radioUnavailable)
        {
            await AppendKnownDevicesDiagnosticsAsync(diagnostics);
        }

        return new BleScanReport
        {
            Devices = map.Values.OrderByDescending(d => d.Rssi).ToList(),
            Diagnostics = string.Join(" | ", diagnostics)
        };
    }

    public static string ToMacAddress(ulong address)
    {
        Span<byte> bytes = stackalloc byte[6];
        for (var i = 0; i < 6; i++)
        {
            bytes[5 - i] = (byte)((address >> (8 * i)) & 0xFF);
        }

        return string.Join(":", bytes.ToArray().Select(b => b.ToString("X2")));
    }

    public static bool TryParseAddress(string input, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var cleaned = new string(input.Where(c => char.IsLetterOrDigit(c)).ToArray());
        if (cleaned.Length != 12 || !ulong.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber, null, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static void MergeInto(Dictionary<ulong, BleScanDevice> map, IEnumerable<BleScanDevice> source)
    {
        foreach (var item in source)
        {
            map[item.BluetoothAddress] = item;
        }
    }

    private static bool IsRadioUnavailableDiagnostic(string? diagnosticLine)
    {
        if (string.IsNullOrWhiteSpace(diagnosticLine))
        {
            return false;
        }

        return diagnosticLine.Contains("RadioNotAvailable", StringComparison.OrdinalIgnoreCase) ||
               diagnosticLine.Contains("adapter=null", StringComparison.OrdinalIgnoreCase) ||
               diagnosticLine.Contains("bluetooth_radio_count=0", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(List<BleScanDevice> Devices, string DiagnosticLine)> ScanByModeAsync(
        BluetoothLEScanningMode mode,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var map = new ConcurrentDictionary<ulong, BleScanDevice>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = mode };
        var receivedCount = 0;
        var startOk = false;
        var stopError = BluetoothError.Success;
        Exception? startException = null;

        void OnReceived(BluetoothLEAdvertisementWatcher _, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            Interlocked.Increment(ref receivedCount);

            var md = new Dictionary<ushort, byte[]>();
            foreach (var section in args.Advertisement.ManufacturerData)
            {
                using var reader = DataReader.FromBuffer(section.Data);
                var bytes = new byte[section.Data.Length];
                reader.ReadBytes(bytes);
                md[(ushort)section.CompanyId] = bytes;
            }

            var uuids = args.Advertisement.ServiceUuids?.Select(u => u).ToList() ?? [];
            var dev = new BleScanDevice
            {
                BluetoothAddress = args.BluetoothAddress,
                Address = ToMacAddress(args.BluetoothAddress),
                Name = args.Advertisement.LocalName ?? string.Empty,
                Rssi = args.RawSignalStrengthInDBm,
                ManufacturerData = md,
                ServiceUuids = uuids
            };

            map.AddOrUpdate(dev.BluetoothAddress, dev, (_, existing) =>
            {
                if (string.IsNullOrWhiteSpace(dev.Name) && !string.IsNullOrWhiteSpace(existing.Name))
                {
                    dev = new BleScanDevice
                    {
                        BluetoothAddress = dev.BluetoothAddress,
                        Address = dev.Address,
                        Name = existing.Name,
                        Rssi = dev.Rssi,
                        ManufacturerData = dev.ManufacturerData.Count > 0 ? dev.ManufacturerData : existing.ManufacturerData,
                        ServiceUuids = dev.ServiceUuids.Count > 0 ? dev.ServiceUuids : existing.ServiceUuids
                    };
                }

                return dev;
            });
        }

        void OnStopped(BluetoothLEAdvertisementWatcher _, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            stopError = args.Error;
        }

        watcher.Received += OnReceived;
        watcher.Stopped += OnStopped;
        try
        {
            try
            {
                watcher.Start();
                startOk = true;
            }
            catch (Exception ex)
            {
                startException = ex;
            }

            if (startOk)
            {
                await Task.Delay(timeout, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        finally
        {
            try
            {
                watcher.Stop();
            }
            catch
            {
                // ignore
            }

            watcher.Received -= OnReceived;
            watcher.Stopped -= OnStopped;
        }

        var statusText = watcher.Status.ToString();
        var errorText = stopError.ToString();
        var exText = startException == null ? "none" : $"{startException.GetType().Name}:{startException.Message}";
        var diag = $"scan_mode={mode} start_ok={startOk} status={statusText} stop_error={errorText} received={receivedCount} unique={map.Count} start_exception={exText}";

        return (map.Values.OrderByDescending(d => d.Rssi).ToList(), diag);
    }

    private static async Task AppendEnvironmentDiagnosticsAsync(List<string> diagnostics)
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null)
            {
                diagnostics.Add("ble_env adapter=null");
            }
            else
            {
                diagnostics.Add($"ble_env adapter_ok=True low_energy={adapter.IsLowEnergySupported} central_role={adapter.IsCentralRoleSupported}");
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add($"ble_env adapter_error={ex.GetType().Name}:{ex.Message}");
        }

        try
        {
            var radios = await Radio.GetRadiosAsync();
            var btRadios = radios.Where(r => r.Kind == RadioKind.Bluetooth).ToList();
            if (btRadios.Count == 0)
            {
                diagnostics.Add("ble_env bluetooth_radio_count=0");
            }
            else
            {
                var parts = btRadios.Select(r => $"{r.Name}:{r.State}");
                diagnostics.Add($"ble_env bluetooth_radios={string.Join(",", parts)}");
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add($"ble_env radio_error={ex.GetType().Name}:{ex.Message}");
        }
    }

    private static async Task AppendKnownDevicesDiagnosticsAsync(List<string> diagnostics)
    {
        try
        {
            var selector = BluetoothLEDevice.GetDeviceSelector();
            var known = await DeviceInformation.FindAllAsync(selector);
            if (known.Count == 0)
            {
                diagnostics.Add("known_ble_devices count=0");
                return;
            }

            var lines = new List<string>();
            foreach (var info in known.Take(10))
            {
                var addr = TryGetStringProperty(info.Properties, "System.Devices.Aep.DeviceAddress");
                var paired = info.Pairing?.IsPaired == true;
                var connected = TryGetBoolProperty(info.Properties, "System.Devices.Aep.IsConnected");
                var name = string.IsNullOrWhiteSpace(info.Name) ? "Unknown" : info.Name;
                lines.Add($"{name}@{addr}(paired={paired},connected={connected})");
            }

            diagnostics.Add($"known_ble_devices count={known.Count} top={string.Join(";", lines)}");
        }
        catch (Exception ex)
        {
            diagnostics.Add($"known_ble_devices error={ex.GetType().Name}:{ex.Message}");
        }
    }

    private static string TryGetStringProperty(IReadOnlyDictionary<string, object> props, string key)
    {
        if (props.TryGetValue(key, out var value) && value != null)
        {
            return value.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static bool TryGetBoolProperty(IReadOnlyDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var value) || value == null)
        {
            return false;
        }

        if (value is bool b)
        {
            return b;
        }

        return bool.TryParse(value.ToString(), out var parsed) && parsed;
    }
}
