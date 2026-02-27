namespace BleWindowsTool.Ble;

public sealed class BleDeviceIdentity
{
    public bool HasDreamPodService { get; init; }
    public bool HasMdskManufacturerData { get; init; }
    public byte? Protocol { get; init; }
    public byte? DeviceType { get; init; }
    public byte? DeviceSubType { get; init; }
    public string ManufacturerMac { get; init; } = string.Empty;
    public bool IsLikelyBox { get; init; }
    public bool IsLikelyRadar { get; init; }
}

public static class BleDeviceIntrospection
{
    public const ushort MdskCompanyId = 0x4D44;
    public const string DreamPodServiceUuid = "534b0001-b5a3-f393-e0a9-68716563686f";

    public static BleDeviceIdentity Inspect(BleScanDevice device)
    {
        var hasService = device.ServiceUuids.Any(u =>
            string.Equals(u.ToString(), DreamPodServiceUuid, StringComparison.OrdinalIgnoreCase));

        var md = ParseMdsk(device.ManufacturerData);
        var isBox = false;
        var isRadar = false;

        if (md.DeviceType.HasValue && md.DeviceSubType.HasValue)
        {
            var type = md.DeviceType.Value;
            var sub = md.DeviceSubType.Value;

            if ((type == 0x21 && sub == 0x80) ||
                (type == 0x02 && sub == 0x01) ||
                (sub == 0x00 && (type == 0x20 || type == 0x21)))
            {
                isBox = true;
            }

            if (type == 0x21 && sub == 0x80)
            {
                isRadar = true;
            }
        }

        return new BleDeviceIdentity
        {
            HasDreamPodService = hasService,
            HasMdskManufacturerData = md.HasData,
            Protocol = md.Protocol,
            DeviceType = md.DeviceType,
            DeviceSubType = md.DeviceSubType,
            ManufacturerMac = md.ManufacturerMac,
            IsLikelyBox = isBox,
            IsLikelyRadar = isRadar
        };
    }

    public static bool NameLooksLikeBox(string? name)
    {
        var low = (name ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(low))
        {
            return false;
        }

        if (low.Contains("eeg") || low.Contains("ecg") || low.Contains("o2"))
        {
            return false;
        }

        if (low.Contains("relay") || low.Contains("box") || low.Contains("charger") || low.Contains("dock") || low.Contains("case"))
        {
            return true;
        }

        return low.Contains("mdsk");
    }

    public static bool NameLooksLikeRadar(string? name)
    {
        var low = (name ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(low))
        {
            return false;
        }

        return low.Contains("mwr") || low.Contains("mmwave") || low.Contains("radar") || low.Contains("mdsk-mwr");
    }

    public static string NormalizeMac(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return new string(input.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    public static string FormatScanLine(BleScanDevice device, BleDeviceIdentity identity)
    {
        var name = string.IsNullOrWhiteSpace(device.Name) ? "Unknown" : device.Name;
        var service = identity.HasDreamPodService ? "yes" : "no";

        var typeText = identity.DeviceType.HasValue ? $"0x{identity.DeviceType.Value:X2}" : "n/a";
        var subText = identity.DeviceSubType.HasValue ? $"0x{identity.DeviceSubType.Value:X2}" : "n/a";
        var protoText = identity.Protocol.HasValue ? $"0x{identity.Protocol.Value:X2}" : "n/a";
        var mfgMac = string.IsNullOrWhiteSpace(identity.ManufacturerMac) ? "n/a" : identity.ManufacturerMac;

        return $"name='{name}' addr={device.Address} rssi={device.Rssi} svc={service} mdsk={identity.HasMdskManufacturerData} proto={protoText} type={typeText} sub={subText} mdsk_mac={mfgMac}";
    }

    private static (bool HasData, byte? Protocol, byte? DeviceType, byte? DeviceSubType, string ManufacturerMac) ParseMdsk(Dictionary<ushort, byte[]> md)
    {
        if (!md.TryGetValue(MdskCompanyId, out var data) || data.Length < 3)
        {
            return (false, null, null, null, string.Empty);
        }

        var protocol = data[0];
        var b1 = data[1];
        var b2 = data[2];
        byte devType;
        byte devSub;

        if (b2 == 0x20 || b2 == 0x21)
        {
            devType = b2;
            devSub = b1;
        }
        else
        {
            devType = b1;
            devSub = b2;
        }

        var mac = string.Empty;
        if (data.Length >= 9)
        {
            mac = string.Join(":", data.Skip(3).Take(6).Select(x => x.ToString("X2")));
        }

        return (true, protocol, devType, devSub, mac);
    }
}
