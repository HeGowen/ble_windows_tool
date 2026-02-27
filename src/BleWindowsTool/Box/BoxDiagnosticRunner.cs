using System.Collections.Concurrent;
using System.Text;
using BleWindowsTool.Ble;
using BleWindowsTool.Config;
using BleWindowsTool.Logging;
using Windows.Foundation;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BleWindowsTool.Box;

public sealed class BoxDiagnosticSummary
{
    public required string StartedAt { get; init; }
    public string EndedAt { get; set; } = string.Empty;
    public string SelectedName { get; set; } = string.Empty;
    public string SelectedAddress { get; set; } = string.Empty;
    public int SessionAttempts { get; set; }
    public int SessionConnected { get; set; }
    public int SessionDisconnected { get; set; }
    public int TotalNotifications { get; set; }
    public int TotalFrames { get; set; }
    public Dictionary<string, int> FuncCounts { get; } = new();
    public Dictionary<string, int> DataTypeCounts { get; } = new();
    public string LastError { get; set; } = "none";
    public string LastDataAt { get; set; } = "n/a";
}

internal sealed class AddressCandidate
{
    public required ulong Address { get; init; }
    public required string AddressText { get; init; }
    public required string Reason { get; init; }
}

public sealed class BoxDiagnosticRunner : IDisposable
{
    private const ushort FuncConnectAll = 0x0011;
    private const ushort FuncCollectAll = 0x0012;
    private const ushort FuncSyncTime = 0x0080;
    private const ushort FuncDataUpload = 0x8000;
    private const ushort FuncStatusReport = 0x8001;

    private const ushort EegType = 0x2010;
    private const ushort EcgType = 0x2020;
    private const ushort O2Type = 0x2030;
    private const ushort AudioOpusType = 0x2000;
    private const ushort AudioSnoreType = 0x2100;
    private const ushort AudioSnoreTypeB = 0x2133;
    private const ushort AudioStatusType = 0x2130;

    private readonly BoxConfig _box;
    private readonly DiagConfig _diag;
    private readonly DiagLogger _logger;
    private readonly object _packetLock = new();
    private readonly StreamWriter _packetWriter;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly FrameStreamParser _parser = new();
    private readonly ConcurrentDictionary<string, int> _funcCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _typeCounts = new(StringComparer.OrdinalIgnoreCase);

    private int _notifyCount;
    private int _frameCount;
    private DateTimeOffset _lastDataAt = DateTimeOffset.MinValue;

    public BoxDiagnosticRunner(BoxConfig box, DiagConfig diag, DiagLogger logger, string packetLogPath)
    {
        _box = box;
        _diag = diag;
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(packetLogPath)!);
        _packetWriter = new StreamWriter(new FileStream(packetLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    public async Task<BoxDiagnosticSummary> RunAsync(BleScanDevice selected, CancellationToken cancellationToken)
    {
        var summary = new BoxDiagnosticSummary
        {
            StartedAt = DateTimeOffset.Now.ToString("O"),
            SelectedName = selected.Name,
            SelectedAddress = selected.Address
        };

        var start = DateTimeOffset.UtcNow;
        var maxDuration = TimeSpan.FromSeconds(Math.Max(10, _diag.SessionSeconds));

        while (DateTimeOffset.UtcNow - start < maxDuration && !cancellationToken.IsCancellationRequested)
        {
            summary.SessionAttempts++;
            var remaining = maxDuration - (DateTimeOffset.UtcNow - start);
            var attemptResult = await RunSessionOnceAsync(selected, remaining, cancellationToken);

            if (attemptResult.Connected)
            {
                summary.SessionConnected++;
            }

            if (attemptResult.Disconnected)
            {
                summary.SessionDisconnected++;
            }

            if (!string.IsNullOrWhiteSpace(attemptResult.Error))
            {
                summary.LastError = attemptResult.Error;
            }

            if (!attemptResult.Connected)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        summary.TotalNotifications = _notifyCount;
        summary.TotalFrames = _frameCount;
        summary.LastDataAt = _lastDataAt == DateTimeOffset.MinValue ? "n/a" : _lastDataAt.ToString("O");
        foreach (var kv in _funcCounts.OrderBy(k => k.Key))
        {
            summary.FuncCounts[kv.Key] = kv.Value;
        }

        foreach (var kv in _typeCounts.OrderBy(k => k.Key))
        {
            summary.DataTypeCounts[kv.Key] = kv.Value;
        }

        summary.EndedAt = DateTimeOffset.Now.ToString("O");
        return summary;
    }

    private async Task<(bool Connected, bool Disconnected, string Error)> RunSessionOnceAsync(
        BleScanDevice selected,
        TimeSpan remaining,
        CancellationToken outerToken)
    {
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        GattCharacteristic? rxChar = null;
        GattCharacteristic? txChar = null;
        TaskCompletionSource<bool>? disconnectTcs = null;
        CancellationTokenSource? sessionCts = null;
        Task? keepaliveTask = null;
        TypedEventHandler<BluetoothLEDevice, object>? connectionHandler = null;
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? valueHandler = null;
        string lastError = string.Empty;

        var maxConnectAttempts = Math.Max(1, _diag.MaxConnectAttempts);
        var openTimeout = TimeSpan.FromSeconds(Math.Max(3, _diag.DeviceOpenTimeoutSeconds));
        var gattTimeout = TimeSpan.FromSeconds(Math.Max(4, _diag.GattTimeoutSeconds));
        var serviceGuid = Guid.Parse(_box.ServiceUuid);
        var rxGuid = Guid.Parse(_box.RxCharUuid);
        var txGuid = Guid.Parse(_box.TxCharUuid);

        var addressCandidates = BuildAddressCandidates(selected);
        _logger.Info($"session connect candidates: {string.Join(", ", addressCandidates.Select(x => $"{x.AddressText}({x.Reason})"))}");

        for (var attempt = 1; attempt <= maxConnectAttempts && !outerToken.IsCancellationRequested; attempt++)
        {
            foreach (var candidate in addressCandidates)
            {
                _logger.Info($"connecting {candidate.AddressText} attempt={attempt}/{maxConnectAttempts} source={candidate.Reason}");
                var openRes = await TryWithTimeoutAsync(
                    () => BluetoothLEDevice.FromBluetoothAddressAsync(candidate.Address).AsTask(),
                    openTimeout,
                    outerToken);

                if (!openRes.Success || openRes.Value == null)
                {
                    lastError = $"device open failed {candidate.AddressText}";
                    continue;
                }

                device = openRes.Value;
                _logger.Info($"device open status addr={candidate.AddressText} state={device.ConnectionStatus}");

                await Task.Delay(TimeSpan.FromMilliseconds(220), outerToken);

                service = await FindServiceAsync(device, serviceGuid, gattTimeout, outerToken);
                if (service == null)
                {
                    _logger.Warn($"service not found addr={candidate.AddressText}");
                    TryDispose(device);
                    device = null;
                    continue;
                }

                rxChar = await FindCharacteristicAsync(device, service, rxGuid, gattTimeout, outerToken);
                txChar = await FindCharacteristicAsync(device, service, txGuid, gattTimeout, outerToken);
                if (rxChar == null || txChar == null)
                {
                    _logger.Warn($"characteristics not found addr={candidate.AddressText}");
                    TryDispose(service);
                    TryDispose(device);
                    service = null;
                    device = null;
                    rxChar = null;
                    txChar = null;
                    continue;
                }

                _logger.Info($"connect path selected addr={candidate.AddressText} source={candidate.Reason}");
                break;
            }

            if (device != null && service != null && rxChar != null && txChar != null)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), outerToken);
        }

        if (device == null || service == null || rxChar == null || txChar == null)
        {
            return (false, false, string.IsNullOrWhiteSpace(lastError) ? "connect_failed" : lastError);
        }

        try
        {
            disconnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnConnectionChanged(BluetoothLEDevice sender, object args)
            {
                if (sender.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    disconnectTcs.TrySetResult(true);
                }
            }

            void OnValueChanged(GattCharacteristic _, GattValueChangedEventArgs args)
            {
                try
                {
                    using var reader = DataReader.FromBuffer(args.CharacteristicValue);
                    var bytes = new byte[args.CharacteristicValue.Length];
                    reader.ReadBytes(bytes);
                    Interlocked.Increment(ref _notifyCount);
                    foreach (var frame in _parser.Append(bytes))
                    {
                        HandleFrame(frame, bytes);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"notify parse error: {ex.Message}");
                }
            }

            connectionHandler = OnConnectionChanged;
            valueHandler = OnValueChanged;
            device.ConnectionStatusChanged += connectionHandler;
            txChar.ValueChanged += valueHandler;

            var notifyRes = await TryWithTimeoutAsync(
                () => txChar.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(),
                gattTimeout,
                outerToken);

            if (!notifyRes.Success || notifyRes.Value != GattCommunicationStatus.Success)
            {
                return (false, false, $"enable_notify_failed:{(notifyRes.Success ? notifyRes.Value : "timeout")}");
            }

            _logger.Info("connected and notifications enabled");
            await SendCommandAsync(rxChar, FuncSyncTime, BuildTimestampPayload(), gattTimeout, outerToken);
            await SendCommandAsync(rxChar, FuncConnectAll, [1], gattTimeout, outerToken);
            await SendCommandAsync(rxChar, FuncCollectAll, BuildCollectPayload(true), gattTimeout, outerToken);

            sessionCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            keepaliveTask = RunKeepAliveLoopAsync(rxChar, gattTimeout, sessionCts.Token);

            var sessionDeadline = TimeSpan.FromSeconds(Math.Max(5, Math.Min(_diag.SessionSeconds, (int)Math.Max(5, remaining.TotalSeconds))));
            var timerTask = Task.Delay(sessionDeadline, sessionCts.Token);
            var completed = await Task.WhenAny(disconnectTcs.Task, timerTask);
            var disconnected = completed == disconnectTcs.Task;

            if (disconnected)
            {
                _logger.Warn("session disconnected by BLE stack");
            }
            else
            {
                _logger.Info("session completed by timeout window");
            }

            return (true, disconnected, disconnected ? "disconnected" : string.Empty);
        }
        finally
        {
            if (sessionCts != null)
            {
                sessionCts.Cancel();
            }

            if (keepaliveTask != null)
            {
                try { await keepaliveTask; } catch { }
            }

            try
            {
                if (txChar != null && valueHandler != null)
                {
                    txChar.ValueChanged -= valueHandler;
                }

                if (device != null && connectionHandler != null)
                {
                    device.ConnectionStatusChanged -= connectionHandler;
                }

                if (txChar != null)
                {
                    await TryWithTimeoutAsync(
                        () => txChar.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(),
                        TimeSpan.FromSeconds(2),
                        CancellationToken.None);
                }
            }
            catch
            {
                // ignore
            }

            TryDispose(service);
            TryDispose(device);
        }
    }

    private List<AddressCandidate> BuildAddressCandidates(BleScanDevice selected)
    {
        var map = new Dictionary<string, AddressCandidate>(StringComparer.OrdinalIgnoreCase);

        void Add(string raw, string reason)
        {
            if (!BleScanner.TryParseAddress(raw, out var parsed))
            {
                return;
            }

            var text = BleScanner.ToMacAddress(parsed);
            if (!map.ContainsKey(text))
            {
                map[text] = new AddressCandidate
                {
                    Address = parsed,
                    AddressText = text,
                    Reason = reason
                };
            }
        }

        Add(selected.Address, "scan_addr");
        var identity = BleDeviceIntrospection.Inspect(selected);
        Add(identity.ManufacturerMac, "mdsk_mfg_mac");
        Add(_box.TargetAddress, "config_target");
        foreach (var addr in _box.AllowAddresses)
        {
            Add(addr, "allow");
        }

        return map.Values.ToList();
    }

    private async Task RunKeepAliveLoopAsync(GattCharacteristic rxChar, TimeSpan timeout, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, _diag.KeepaliveCollectSeconds)), ct);
            await SendCommandAsync(rxChar, FuncCollectAll, BuildCollectPayload(true), timeout, ct);
            _logger.Info("keepalive collect sent");
        }
    }

    private void HandleFrame(BleFrame frame, byte[] rawNotifyChunk)
    {
        Interlocked.Increment(ref _frameCount);
        AddCount(_funcCounts, $"0x{frame.FunctionCode:X4}");
        _lastDataAt = DateTimeOffset.Now;

        if (_diag.DumpRawHex)
        {
            WritePacketLine($"raw_notify len={rawNotifyChunk.Length} hex={Convert.ToHexString(rawNotifyChunk)}");
        }

        if (frame.FunctionCode == FuncDataUpload)
        {
            var stld = FrameCodec.ParseStld(frame.Payload);
            foreach (var item in stld)
            {
                AddCount(_typeCounts, $"0x{item.Type:X4}");
                var hint = item.Type switch
                {
                    EegType => $"eeg bytes={item.Data.Length}",
                    EcgType => $"ecg bytes={item.Data.Length}",
                    O2Type => $"o2 bytes={item.Data.Length}",
                    AudioOpusType => $"audio_opus bytes={item.Data.Length}",
                    AudioSnoreType => $"audio_snore bytes={item.Data.Length}",
                    AudioSnoreTypeB => $"audio_snore_b bytes={item.Data.Length}",
                    _ => $"type=0x{item.Type:X4} bytes={item.Data.Length}"
                };
                WritePacketLine($"frame=0x{frame.FunctionCode:X4} seq={item.Sequence} {hint}");
            }
        }
        else if (frame.FunctionCode == FuncStatusReport)
        {
            var tld = FrameCodec.ParseTld(frame.Payload);
            foreach (var item in tld)
            {
                AddCount(_typeCounts, $"status_0x{item.Type:X4}");
                var detail = item.Type switch
                {
                    EegType when item.Data.Length >= 3 => $"eeg_status ble={item.Data[0]} battery={item.Data[1]} loff={item.Data[2]}",
                    EcgType when item.Data.Length >= 3 => $"ecg_status ble={item.Data[0]} battery={item.Data[1]} loff={item.Data[2]}",
                    O2Type when item.Data.Length >= 3 => $"o2_status ble={item.Data[0]} battery={item.Data[1]} wear={item.Data[2]}",
                    AudioStatusType when item.Data.Length >= 2 => $"audio_status ble={item.Data[0]} battery={item.Data[1]}",
                    _ => $"status_type=0x{item.Type:X4} bytes={item.Data.Length}"
                };
                WritePacketLine($"frame=0x{frame.FunctionCode:X4} {detail}");
            }
        }
        else
        {
            WritePacketLine($"frame=0x{frame.FunctionCode:X4} payload_bytes={frame.Payload.Length}");
        }

        var currentFrames = Volatile.Read(ref _frameCount);
        if (currentFrames % 20 == 0)
        {
            _logger.Info($"data heartbeat notifications={_notifyCount} frames={currentFrames}");
        }
    }

    private async Task SendCommandAsync(
        GattCharacteristic rxChar,
        ushort functionCode,
        byte[] payload,
        TimeSpan timeout,
        CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var frame = FrameCodec.BuildFrame(functionCode, payload);
            var writer = new DataWriter();
            writer.WriteBytes(frame);
            var buffer = writer.DetachBuffer();

            var w1 = await TryWithTimeoutAsync(
                () => rxChar.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse).AsTask(),
                timeout,
                ct);

            if (!w1.Success || w1.Value != GattCommunicationStatus.Success)
            {
                var w2 = await TryWithTimeoutAsync(
                    () => rxChar.WriteValueAsync(buffer, GattWriteOption.WriteWithResponse).AsTask(),
                    timeout,
                    ct);

                if (!w2.Success || w2.Value != GattCommunicationStatus.Success)
                {
                    _logger.Warn($"write failed func=0x{functionCode:X4} status={(w2.Success ? w2.Value : "timeout")}");
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<GattDeviceService?> FindServiceAsync(BluetoothLEDevice device, Guid serviceGuid, TimeSpan timeout, CancellationToken ct)
    {
        foreach (var mode in new[] { BluetoothCacheMode.Uncached, BluetoothCacheMode.Cached })
        {
            var result = await TryWithTimeoutAsync(
                () => device.GetGattServicesForUuidAsync(serviceGuid, mode).AsTask(),
                timeout,
                ct);

            if (!result.Success || result.Value == null)
            {
                continue;
            }

            if (result.Value.Status != GattCommunicationStatus.Success)
            {
                _logger.Warn($"GetGattServicesForUuid status={result.Value.Status} mode={mode}");
                continue;
            }

            if (result.Value.Services.Count > 0)
            {
                return result.Value.Services[0];
            }
        }

        return null;
    }

    private async Task<GattCharacteristic?> FindCharacteristicAsync(BluetoothLEDevice device, GattDeviceService service, Guid charGuid, TimeSpan timeout, CancellationToken ct)
    {
        foreach (var mode in new[] { BluetoothCacheMode.Uncached, BluetoothCacheMode.Cached })
        {
            var result = await TryWithTimeoutAsync(
                () => service.GetCharacteristicsForUuidAsync(charGuid, mode).AsTask(),
                timeout,
                ct);

            if (!result.Success || result.Value == null)
            {
                continue;
            }

            if (result.Value.Status != GattCommunicationStatus.Success)
            {
                _logger.Warn($"GetCharacteristicsForUuid status={result.Value.Status} mode={mode}");
                continue;
            }

            if (result.Value.Characteristics.Count > 0)
            {
                return result.Value.Characteristics[0];
            }
        }

        foreach (var mode in new[] { BluetoothCacheMode.Uncached, BluetoothCacheMode.Cached })
        {
            var allServices = await TryWithTimeoutAsync(
                () => device.GetGattServicesAsync(mode).AsTask(),
                timeout,
                ct);

            if (!allServices.Success || allServices.Value == null || allServices.Value.Status != GattCommunicationStatus.Success)
            {
                continue;
            }

            foreach (var svc in allServices.Value.Services)
            {
                var chars = await TryWithTimeoutAsync(
                    () => svc.GetCharacteristicsForUuidAsync(charGuid, mode).AsTask(),
                    timeout,
                    ct);

                if (!chars.Success || chars.Value == null || chars.Value.Status != GattCommunicationStatus.Success)
                {
                    continue;
                }

                if (chars.Value.Characteristics.Count > 0)
                {
                    return chars.Value.Characteristics[0];
                }
            }
        }

        return null;
    }

    private static byte[] BuildCollectPayload(bool enable, long timestamp = 0)
    {
        var payload = new byte[9];
        payload[0] = enable ? (byte)1 : (byte)0;
        BitConverter.GetBytes(timestamp).CopyTo(payload, 1);
        return payload;
    }

    private static byte[] BuildTimestampPayload(long? timestamp = null)
    {
        var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return BitConverter.GetBytes(ts);
    }

    private void AddCount(ConcurrentDictionary<string, int> map, string key)
    {
        map.AddOrUpdate(key, 1, (_, old) => old + 1);
    }

    private void WritePacketLine(string line)
    {
        var full = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {line}";
        lock (_packetLock)
        {
            _packetWriter.WriteLine(full);
        }
    }

    private static void TryDispose(IDisposable? disposable)
    {
        try { disposable?.Dispose(); } catch { }
    }

    private static async Task<(bool Success, T? Value)> TryWithTimeoutAsync<T>(
        Func<Task<T>> taskFactory,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = taskFactory();
        var delay = Task.Delay(timeout, timeoutCts.Token);
        var completed = await Task.WhenAny(task, delay);
        if (completed == task)
        {
            timeoutCts.Cancel();
            return (true, await task);
        }

        return (false, default);
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        lock (_packetLock)
        {
            _packetWriter.Dispose();
        }
    }
}
