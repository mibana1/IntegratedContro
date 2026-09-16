using System.Buffers.Binary;

namespace IntegratedContro.Core;

public static class PreviewImageLimits
{
    public static (int Width, int Height) Dimensions(byte[] data)
    {
        if (data.Length > MediaLimits.ImageBytes) throw MediaLimits.Invalid();
        int width = 0, height = 0;
        if (data.Length >= 33 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            data.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
        }
        else if (data.Length > 4 && data[0] == 255 && data[1] == 216)
        {
            for (var i = 2; i + 3 < data.Length;)
            {
                if (data[i++] != 255) throw MediaLimits.Invalid();
                while (i < data.Length && data[i] == 255) i++;
                if (i >= data.Length) break;
                var marker = data[i++];
                if (marker is 0xD8 or 0xD9 or 0x01 || marker is >= 0xD0 and <= 0xD7) continue;
                if (i + 2 > data.Length) break;
                var size = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i, 2));
                if (size < 2 || i + size > data.Length) break;
                if (marker is 0xC0 or 0xC1 or 0xC2)
                {
                    if (size < 8) break;
                    height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 3, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 5, 2)); break;
                }
                if (marker == 0xDA) break;
                i += size;
            }
        }
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || (long)width * height > 4 * 1024 * 1024)
            throw MediaLimits.Invalid();
        return (width, height);
    }
}
