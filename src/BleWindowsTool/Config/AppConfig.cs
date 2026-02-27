using System.Text.Json;
using System.Text.Json.Serialization;
using BleWindowsTool.Logging;

namespace BleWindowsTool.Config;

public sealed class AppConfig
{
    [JsonPropertyName("box")]
    public BoxConfig Box { get; set; } = new();

    [JsonPropertyName("radar")]
    public RadarConfig Radar { get; set; } = new();

    [JsonPropertyName("diag")]
    public DiagConfig Diag { get; set; } = new();

    public static AppConfig LoadOrCreate(string path, DiagLogger logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            var defaults = new AppConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptionsIndented()));
            logger.Info($"config not found, created default config: {path}");
            return defaults;
        }

        try
        {
            var text = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(text, JsonOptions());
            if (cfg == null)
            {
                logger.Warn("config parse produced null, using defaults");
                return new AppConfig();
            }

            return cfg;
        }
        catch (Exception ex)
        {
            logger.Warn($"config parse failed, using defaults: {ex.Message}");
            return new AppConfig();
        }
    }

    public static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
    }

    public static JsonSerializerOptions JsonOptionsIndented()
    {
        var options = JsonOptions();
        options.WriteIndented = true;
        return options;
    }
}

public sealed class BoxConfig
{
    [JsonPropertyName("target_name_contains")]
    public string TargetNameContains { get; set; } = "MDSK-RELAY";

    [JsonPropertyName("target_address")]
    public string TargetAddress { get; set; } = string.Empty;

    [JsonPropertyName("allow_addresses")]
    public List<string> AllowAddresses { get; set; } = [];

    [JsonPropertyName("service_uuid")]
    public string ServiceUuid { get; set; } = "534b0001-b5a3-f393-e0a9-68716563686f";

    [JsonPropertyName("rx_char_uuid")]
    public string RxCharUuid { get; set; } = "534b0002-b5a3-f393-e0a9-68716563686f";

    [JsonPropertyName("tx_char_uuid")]
    public string TxCharUuid { get; set; } = "534b0003-b5a3-f393-e0a9-68716563686f";
}

public sealed class RadarConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("target_name_contains")]
    public string TargetNameContains { get; set; } = "MDSK-MWR";

    [JsonPropertyName("target_address")]
    public string TargetAddress { get; set; } = string.Empty;

    [JsonPropertyName("allow_addresses")]
    public List<string> AllowAddresses { get; set; } = [];

    [JsonPropertyName("service_uuid")]
    public string ServiceUuid { get; set; } = "534b0001-b5a3-f393-e0a9-68716563686f";

    [JsonPropertyName("rx_char_uuid")]
    public string RxCharUuid { get; set; } = "534b0002-b5a3-f393-e0a9-68716563686f";

    [JsonPropertyName("tx_char_uuid")]
    public string TxCharUuid { get; set; } = "534b0003-b5a3-f393-e0a9-68716563686f";
}

public sealed class DiagConfig
{
    [JsonPropertyName("scan_timeout_seconds")]
    public double ScanTimeoutSeconds { get; set; } = 6;

    [JsonPropertyName("scan_attempts")]
    public int ScanAttempts { get; set; } = 3;

    [JsonPropertyName("session_seconds")]
    public int SessionSeconds { get; set; } = 180;

    [JsonPropertyName("max_connect_attempts")]
    public int MaxConnectAttempts { get; set; } = 5;

    [JsonPropertyName("device_open_timeout_seconds")]
    public double DeviceOpenTimeoutSeconds { get; set; } = 10;

    [JsonPropertyName("gatt_timeout_seconds")]
    public double GattTimeoutSeconds { get; set; } = 12;

    [JsonPropertyName("keepalive_collect_seconds")]
    public double KeepaliveCollectSeconds { get; set; } = 10;

    [JsonPropertyName("dump_raw_hex")]
    public bool DumpRawHex { get; set; } = false;
}
