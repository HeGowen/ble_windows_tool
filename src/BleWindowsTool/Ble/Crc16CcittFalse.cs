namespace BleWindowsTool.Ble;

public static class Crc16CcittFalse
{
    private const ushort Poly = 0x1021;
    private const ushort Init = 0xFFFF;

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        var crc = Init;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ Poly)
                    : (ushort)(crc << 1);
            }
        }

        return crc;
    }
}
