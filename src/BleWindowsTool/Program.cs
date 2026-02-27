using System.Text.Json;
using BleWindowsTool.Ble;
using BleWindowsTool.Box;
using BleWindowsTool.Config;
using BleWindowsTool.Diagnostics;
using BleWindowsTool.Logging;

namespace BleWindowsTool;

internal static class Program
{
    private const string InstanceMutexName = @"Global\DreamPod.BleWindowsTool.SingleInstance";

    private sealed class BoxScanCandidate
    {
        public required BleScanDevice Device { get; init; }
        public required BleDeviceIdentity Identity { get; init; }
        public required bool ExplicitAddressMatch { get; init; }
        public required bool AllowAddressMatch { get; init; }
        public required bool NameMatch { get; init; }
        public required bool BoxNameMatch { get; init; }
        public required bool RadarLikeName { get; init; }
        public required bool HeuristicMatch { get; init; }
        public required int Score { get; init; }
        public required string Reason { get; init; }
    }

    private static async Task<int> Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        var baseDir = AppContext.BaseDirectory;
        var logRoot = !string.IsNullOrWhiteSpace(options.LogRoot)
            ? options.LogRoot
            : Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(logRoot);
        var runId = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
        var runDir = Path.Combine(logRoot, $"run_{runId}");
        Directory.CreateDirectory(runDir);

        var runtimeLog = Path.Combine(runDir, "runtime.log");
        using var logger = new DiagLogger(runtimeLog);
        logger.Info("=== DreamPod BLE Windows Diagnostic Tool ===");
        logger.Info($"run_dir={runDir}");

        var createdNew = false;
        using var mutex = new Mutex(true, InstanceMutexName, out createdNew);
        if (!createdNew)
        {
            logger.Warn("another ble_windows_tool instance is running. exit.");
            return 10;
        }

        var configPath = !string.IsNullOrWhiteSpace(options.ConfigPath)
            ? options.ConfigPath
            : Path.Combine(baseDir, "config", "box_diag_config.json");
        var config = AppConfig.LoadOrCreate(configPath, logger);
        if (!string.IsNullOrWhiteSpace(options.AddressOverride))
        {
            config.Box.TargetAddress = options.AddressOverride;
        }

        if (!string.IsNullOrWhiteSpace(options.NameOverride))
        {
            config.Box.TargetNameContains = options.NameOverride;
        }

        if (options.SessionSecondsOverride > 0)
        {
            config.Diag.SessionSeconds = options.SessionSecondsOverride;
        }

        if (options.ScanAttemptsOverride > 0)
        {
            config.Diag.ScanAttempts = options.ScanAttemptsOverride;
        }

        await File.WriteAllTextAsync(Path.Combine(runDir, "effective_config.json"), JsonSerializer.Serialize(config, AppConfig.JsonOptionsIndented()));
        logger.Info($"config_path={configPath}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await BluetoothHealthReporter.CollectAsync(logger, Path.Combine(runDir, "bluetooth_health.json"));

            var target = await SelectTargetAsync(config, logger, runDir, cts.Token);
            if (target == null)
            {
                logger.Error("charging box not found; see scan_attempts.log and bluetooth_health.json");
                return 2;
            }

            logger.Info($"selected target: {target.Name} ({target.Address})");
            if (options.ScanOnly)
            {
                logger.Info("scan-only mode enabled; exit after selection.");
                return 0;
            }

            var packetLog = Path.Combine(runDir, "packets.log");
            using var runner = new BoxDiagnosticRunner(config.Box, config.Diag, logger, packetLog);
            var summary = await runner.RunAsync(target, cts.Token);
            await File.WriteAllTextAsync(Path.Combine(runDir, "summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

            logger.Info("diagnostic run completed");
            logger.Info($"summary_file={Path.Combine(runDir, "summary.json")}");
            logger.Info($"packet_log={packetLog}");
            logger.Info($"runtime_log={runtimeLog}");
            Console.WriteLine();
            Console.WriteLine($"Done. Logs: {runDir}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            logger.Warn("cancelled by user");
            return 130;
        }
        catch (Exception ex)
        {
            logger.Exception("fatal", ex);
            return 1;
        }
    }

    private static async Task<BleScanDevice?> SelectTargetAsync(
        AppConfig config,
        DiagLogger logger,
        string runDir,
        CancellationToken ct)
    {
        var scanAttempts = Math.Max(1, config.Diag.ScanAttempts);
        var scanTimeout = TimeSpan.FromSeconds(Math.Max(2, config.Diag.ScanTimeoutSeconds));
        var allowSet = BuildAllowAddressSet(config.Box.AllowAddresses);
        var scanLogPath = Path.Combine(runDir, "scan_attempts.log");

        logger.Info($"selection config: attempts={scanAttempts} timeout_s={scanTimeout.TotalSeconds:0.###} target_name={config.Box.TargetNameContains} target_addr={config.Box.TargetAddress}");

        for (var attempt = 1; attempt <= scanAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var report = await BleScanner.ScanWithReportAsync(scanTimeout, ct);
            var candidates = BuildCandidates(report.Devices, config.Box, allowSet);

            await AppendScanAttemptAsync(scanLogPath, attempt, scanAttempts, report.Diagnostics, candidates);
            logger.Info($"scan attempt {attempt}/{scanAttempts} found={candidates.Count} diag={report.Diagnostics}");
            foreach (var top in candidates.OrderByDescending(c => c.Score).Take(8))
            {
                logger.Info($"scan_top score={top.Score} reason={top.Reason} {BleDeviceIntrospection.FormatScanLine(top.Device, top.Identity)}");
            }

            var selected = candidates
                .Where(c => c.HeuristicMatch)
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.Device.Rssi)
                .FirstOrDefault();

            if (selected != null)
            {
                return selected.Device;
            }

            await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
        }

        return null;
    }

    private static List<BoxScanCandidate> BuildCandidates(
        List<BleScanDevice> devices,
        BoxConfig box,
        HashSet<string> allowSet)
    {
        var list = new List<BoxScanCandidate>(devices.Count);
        var explicitAddressNorm = BleDeviceIntrospection.NormalizeMac(box.TargetAddress);
        var targetName = (box.TargetNameContains ?? string.Empty).Trim();

        foreach (var dev in devices)
        {
            var identity = BleDeviceIntrospection.Inspect(dev);
            var addrNorm = BleDeviceIntrospection.NormalizeMac(dev.Address);
            var explicitAddressMatch = !string.IsNullOrWhiteSpace(explicitAddressNorm) && explicitAddressNorm == addrNorm;
            var allowMatch = allowSet.Contains(addrNorm);
            var nameMatch = string.IsNullOrWhiteSpace(targetName) || (!string.IsNullOrWhiteSpace(dev.Name) && dev.Name.Contains(targetName, StringComparison.OrdinalIgnoreCase));
            var boxName = BleDeviceIntrospection.NameLooksLikeBox(dev.Name);
            var radarName = BleDeviceIntrospection.NameLooksLikeRadar(dev.Name);

            var heuristicMatch =
                explicitAddressMatch ||
                allowMatch ||
                ((identity.IsLikelyBox || identity.HasDreamPodService || boxName) && nameMatch && !(radarName && !boxName));

            var reasons = new List<string>();
            if (explicitAddressMatch) reasons.Add("explicit_addr");
            if (allowMatch) reasons.Add("allow_addr");
            if (nameMatch && !string.IsNullOrWhiteSpace(targetName)) reasons.Add("name_match");
            if (identity.IsLikelyBox) reasons.Add("mfg_box");
            if (identity.HasDreamPodService) reasons.Add("svc");
            if (boxName) reasons.Add("name_box");
            if (radarName) reasons.Add("name_radar");
            if (heuristicMatch) reasons.Add("heuristic");
            if (reasons.Count == 0) reasons.Add("none");

            var score = 0;
            if (explicitAddressMatch) score += 250;
            if (allowMatch) score += 170;
            if (nameMatch) score += 45;
            if (identity.IsLikelyBox) score += 120;
            if (identity.HasDreamPodService) score += 90;
            if (boxName) score += 60;
            if (radarName && !boxName) score -= 100;
            score += Math.Clamp(dev.Rssi + 100, 0, 100) / 10;

            list.Add(new BoxScanCandidate
            {
                Device = dev,
                Identity = identity,
                ExplicitAddressMatch = explicitAddressMatch,
                AllowAddressMatch = allowMatch,
                NameMatch = nameMatch,
                BoxNameMatch = boxName,
                RadarLikeName = radarName,
                HeuristicMatch = heuristicMatch,
                Score = score,
                Reason = string.Join("+", reasons)
            });
        }

        return list;
    }

    private static HashSet<string> BuildAllowAddressSet(IEnumerable<string> addresses)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in addresses)
        {
            var norm = BleDeviceIntrospection.NormalizeMac(raw);
            if (norm.Length == 12)
            {
                set.Add(norm);
            }
        }

        return set;
    }

    private static async Task AppendScanAttemptAsync(
        string scanLogPath,
        int attempt,
        int total,
        string diagnostics,
        List<BoxScanCandidate> candidates)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(scanLogPath)!);
        await using var writer = new StreamWriter(new FileStream(scanLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        await writer.WriteLineAsync($"=== scan attempt {attempt}/{total} at {DateTimeOffset.Now:O} ===");
        await writer.WriteLineAsync($"diagnostics: {diagnostics}");
        foreach (var c in candidates.OrderByDescending(x => x.Score))
        {
            await writer.WriteLineAsync($"score={c.Score} heuristic={c.HeuristicMatch} reason={c.Reason} {BleDeviceIntrospection.FormatScanLine(c.Device, c.Identity)}");
        }

        await writer.WriteLineAsync();
    }
}
