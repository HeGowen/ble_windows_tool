namespace BleWindowsTool.Ble;

public sealed record BleFrame(ushort FunctionCode, byte[] Payload);
public sealed record StldItem(ushort Sequence, ushort Type, byte[] Data);
public sealed record TldItem(ushort Type, byte[] Data);

public static class FrameCodec
{
    public static byte[] BuildFrame(ushort functionCode, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[4 + payload.Length + 2];
        frame[0] = (byte)(functionCode & 0xFF);
        frame[1] = (byte)(functionCode >> 8);
        frame[2] = (byte)(payload.Length & 0xFF);
        frame[3] = (byte)(payload.Length >> 8);
        payload.CopyTo(frame.AsSpan(4));

        var crc = Crc16CcittFalse.Compute(frame.AsSpan(0, 4 + payload.Length));
        frame[4 + payload.Length] = (byte)(crc & 0xFF);
        frame[5 + payload.Length] = (byte)(crc >> 8);
        return frame;
    }

    public static List<StldItem> ParseStld(ReadOnlySpan<byte> payload)
    {
        var items = new List<StldItem>();
        var offset = 0;
        while (offset + 6 <= payload.Length)
        {
            var seq = BitConverter.ToUInt16(payload.Slice(offset, 2));
            var type = BitConverter.ToUInt16(payload.Slice(offset + 2, 2));
            var len = BitConverter.ToUInt16(payload.Slice(offset + 4, 2));
            var start = offset + 6;
            var end = start + len;
            if (end > payload.Length)
            {
                break;
            }

            items.Add(new StldItem(seq, type, payload.Slice(start, len).ToArray()));
            offset = end;
        }

        return items;
    }

    public static List<TldItem> ParseTld(ReadOnlySpan<byte> payload)
    {
        var items = new List<TldItem>();
        var offset = 0;
        while (offset + 4 <= payload.Length)
        {
            var type = BitConverter.ToUInt16(payload.Slice(offset, 2));
            var len = BitConverter.ToUInt16(payload.Slice(offset + 2, 2));
            var start = offset + 4;
            var end = start + len;
            if (end > payload.Length)
            {
                break;
            }

            items.Add(new TldItem(type, payload.Slice(start, len).ToArray()));
            offset = end;
        }

        return items;
    }
}

public sealed class FrameStreamParser
{
    private readonly List<byte> _buffer = [];

    public List<BleFrame> Append(ReadOnlySpan<byte> chunk)
    {
        if (!chunk.IsEmpty)
        {
            _buffer.AddRange(chunk.ToArray());
        }

        var frames = new List<BleFrame>();
        while (_buffer.Count >= 6)
        {
            var functionCode = (ushort)(_buffer[0] | (_buffer[1] << 8));
            var len = (ushort)(_buffer[2] | (_buffer[3] << 8));
            var total = 4 + len + 2;
            if (total > 65535)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            if (_buffer.Count < total)
            {
                break;
            }

            var frame = _buffer.Take(total).ToArray();
            var crcRecv = BitConverter.ToUInt16(frame.AsSpan(4 + len, 2));
            var crcCalc = Crc16CcittFalse.Compute(frame.AsSpan(0, 4 + len));
            if (crcCalc != crcRecv)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            _buffer.RemoveRange(0, total);
            frames.Add(new BleFrame(functionCode, frame.AsSpan(4, len).ToArray()));
        }

        return frames;
    }
}
