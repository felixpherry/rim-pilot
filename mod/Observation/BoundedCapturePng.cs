using System;
using RimPilot.Protocol;

namespace RimPilot.Mod.Observation
{
    // RGB24, no mipmaps; stored DEFLATE blocks avoid an opaque native encoder allocation.
    // Exact output size is known BEFORE allocating any image resource. No scaling/cropping.
    internal sealed class CaptureSize
    {
        internal readonly int Width, Height, RawBytes, PngBytes;
        internal CaptureSize(int width, int height)
        {
            if (width < 1 || height < 1 || width > WireLimits.CaptureWidth || height > WireLimits.CaptureHeight)
                throw new WireException(WireError.ResponseTooLarge);
            long pixels = (long)width * height;
            long filtered = pixels * 3 + height;
            long png = 63 + filtered + 5 * ((filtered + 65534) / 65535);
            if (pixels > WireLimits.CapturePixels || png > WireLimits.CapturePngBytes)
                throw new WireException(WireError.ResponseTooLarge);
            Width = width; Height = height; RawBytes = (int)pixels * 3; PngBytes = (int)png;
        }
    }

    internal static class BoundedCapturePng
    {
        private static readonly uint[] crcTable = MakeCrcTable();
        internal static byte[] Encode(CaptureSize size, byte[] bottomUpRgb, Func<bool> live)
        {
            if (bottomUpRgb.Length != size.RawBytes) throw new WireException(WireError.Unavailable);
            var output = new byte[size.PngBytes];
            int p = 0;
            foreach (byte b in new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) output[p++] = b;
            int ihdr = Begin(output, ref p, 13, "IHDR");
            U32(output, ref p, (uint)size.Width); U32(output, ref p, (uint)size.Height);
            output[p++] = 8; output[p++] = 2; p += 3; End(output, ref p, ihdr);
            int filtered = size.RawBytes + size.Height;
            int deflateBytes = filtered + 5 * ((filtered + 65534) / 65535);
            int idat = Begin(output, ref p, deflateBytes + 6, "IDAT");
            output[p++] = 0x78; output[p++] = 0x01; // zlib, stored blocks, no dictionary
            uint a = 1, bsum = 0;
            int done = 0, stride = size.Width * 3;
            while (done < filtered)
            {
                if (!live()) throw new WireException(WireError.Timeout);
                int length = Math.Min(65535, filtered - done), blockEnd = done + length;
                output[p++] = (byte)(blockEnd == filtered ? 1 : 0);
                output[p++] = (byte)length; output[p++] = (byte)(length >> 8);
                int inverse = length ^ 65535;
                output[p++] = (byte)inverse; output[p++] = (byte)(inverse >> 8);
                // Row segments (filter byte 0, then top-down rows from bottom-up data) copied in bulk:
                // per-byte division/modulo cost far more than the 100-ms stage allows on Mono.
                while (done < blockEnd)
                {
                    int row = done / (stride + 1), col = done % (stride + 1);
                    if (col == 0) { output[p++] = 0; bsum = (bsum + a) % 65521; done++; continue; }
                    int count = Math.Min(blockEnd - done, stride - (col - 1));
                    Buffer.BlockCopy(bottomUpRgb, (size.Height - 1 - row) * stride + col - 1, output, p, count);
                    Adler(output, p, count, ref a, ref bsum); p += count; done += count;
                }
            }
            U32(output, ref p, (bsum << 16) | a); End(output, ref p, idat);
            int iend = Begin(output, ref p, 0, "IEND"); End(output, ref p, iend);
            if (!live()) throw new WireException(WireError.Timeout);
            if (p != output.Length) throw new InvalidOperationException();
            return output;
        }
        // Deferred modulo: at most 5552 bytes between reductions keeps both sums within uint.
        private static void Adler(byte[] bytes, int offset, int count, ref uint a, ref uint b)
        {
            while (count > 0)
            {
                int n = Math.Min(count, 5552);
                for (int i = 0; i < n; i++) { a += bytes[offset + i]; b += a; }
                a %= 65521; b %= 65521; offset += n; count -= n;
            }
        }
        private static int Begin(byte[] bytes, ref int p, int length, string type)
        {
            U32(bytes, ref p, (uint)length); int start = p;
            for (int i = 0; i < 4; i++) bytes[p++] = (byte)type[i];
            return start;
        }
        private static void End(byte[] bytes, ref int p, int start)
        {
            uint crc = uint.MaxValue;
            for (int i = start; i < p; i++) crc = crcTable[(crc ^ bytes[i]) & 255] ^ (crc >> 8);
            U32(bytes, ref p, crc ^ uint.MaxValue);
        }
        private static void U32(byte[] bytes, ref int p, uint value)
        { bytes[p++] = (byte)(value >> 24); bytes[p++] = (byte)(value >> 16); bytes[p++] = (byte)(value >> 8); bytes[p++] = (byte)value; }
        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < table.Length; n++)
            { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) == 1 ? 0xedb88320U ^ (c >> 1) : c >> 1; table[n] = c; }
            return table;
        }
    }
}
