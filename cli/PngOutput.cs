using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;
using RimPilot.Protocol;

namespace RimPilot.Cli;

internal static class PngOutput
{
    // Accept bounded non-interlaced RGB/RGBA8 PNGs. No image library/native decoder or
    // unbounded decompression. CRC, zlib/Adler, scanline filters and exact EOF all checked.
    internal static byte[] Decode(JToken data)
    {
        int declared = (int)data["bytes"]!;
        if (declared < 8 || declared > WireLimits.CapturePngBytes) Reject();
        var png = new byte[declared];
        int written = 0;
        foreach (JToken token in (JArray)data["chunks"]!)
        {
            string text = (string)token!;
            if (!Convert.TryFromBase64String(text, png.AsSpan(written), out int count)) Reject();
            // Canonical base64, including zero padding bits (not merely decoder acceptance).
            if (Convert.ToBase64String(png, written, count) != text) Reject();
            written += count;
        }
        if (written != declared) Reject();
        Validate(png, (int)data["width"]!, (int)data["height"]!);
        return png;
    }

    internal static void Validate(byte[] png, int width, int height)
    {
        if (png.Length < 8 || png.Length > WireLimits.CapturePngBytes || width < 1 || height < 1
            || width > WireLimits.CaptureWidth || height > WireLimits.CaptureHeight
            || (long)width * height > WireLimits.CapturePixels) Reject();
        byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        for (int i = 0; i < 8; i++) if (png[i] != signature[i]) Reject();
        int p = 8, channels = 0;
        bool header = false, image = false, end = false;
        using var compressed = new MemoryStream();
        while (p < png.Length)
        {
            if (png.Length - p < 12) Reject();
            uint length = U32(png, p);
            if (length > png.Length - p - 12) Reject();
            int size = (int)length;
            string type = Encoding.ASCII.GetString(png, p + 4, 4);
            if (Crc(png, p + 4, size + 4) != U32(png, p + size + 8)) Reject();
            int start = p + 8;
            if (!header && type != "IHDR") Reject();
            switch (type)
            {
                case "IHDR":
                    if (header || size != 13 || U32(png, start) != width || U32(png, start + 4) != height
                        || png[start + 8] != 8 || png[start + 9] != 2 && png[start + 9] != 6
                        || png[start + 10] != 0 || png[start + 11] != 0 || png[start + 12] != 0) Reject();
                    channels = png[start + 9] == 2 ? 3 : 4; header = true; break;
                case "IDAT":
                    image = true; compressed.Write(png, start, size); break;
                case "IEND":
                    if (!image || size != 0 || p + 12 != png.Length) Reject();
                    end = true; break;
                // Product emits only these chunks. Unknown metadata/critical chunks fail closed.
                default: Reject(); break;
            }
            p += size + 12;
        }
        if (!end) Reject();
        byte[] zlib = compressed.ToArray();
        if (zlib.Length < 6 || (zlib[0] & 15) != 8 || zlib[0] >> 4 > 7
            || ((zlib[0] << 8) + zlib[1]) % 31 != 0 || (zlib[1] & 32) != 0) Reject();
        // One-byte input prevents DeflateStream read-ahead hiding trailing compressed garbage.
        using var input = new ExactDeflateInput(zlib, 2, zlib.Length - 4);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress, true);
        int stride = checked(width * channels + 1);
        var row = new byte[stride];
        uint a = 1, b = 0;
        try
        {
            for (int y = 0; y < height; y++)
            {
                int offset = 0;
                while (offset < stride)
                {
                    int read = inflater.Read(row, offset, stride - offset);
                    if (read == 0) Reject();
                    offset += read;
                }
                if (row[0] > 4) Reject();
                foreach (byte value in row) { a = (a + value) % 65521; b = (b + a) % 65521; }
            }
            if (inflater.ReadByte() != -1 || input.Position != zlib.Length - 4
                || ((b << 16) | a) != U32(zlib, zlib.Length - 4)) Reject();
        }
        catch (InvalidDataException) { Reject(); }
    }

    // Never overwrite existing files/symlinks. Stage in destination directory, atomically
    // publish complete bytes, remove only owned temporary file on EVERY failure.
    internal static void Write(string path, byte[] png)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        string destination = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".rim-pilot-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool owned = false;
        try
        {
            using (var file = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew,
                Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            {
                owned = true; file.Write(png); file.Flush(true);
            }
            File.Move(temporary, destination, false);
            owned = false;
        }
        finally { if (owned) File.Delete(temporary); }
    }

    private static uint U32(byte[] bytes, int p) => (uint)bytes[p] << 24 | (uint)bytes[p + 1] << 16 | (uint)bytes[p + 2] << 8 | bytes[p + 3];
    private static uint Crc(byte[] bytes, int start, int count)
    {
        uint crc = uint.MaxValue;
        for (int i = start; i < start + count; i++)
        {
            crc ^= bytes[i];
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ uint.MaxValue;
    }
    private static void Reject() => throw new WireException(WireError.InvalidRequest);

    private sealed class ExactDeflateInput(byte[] bytes, int start, int end) : Stream
    {
        private int position = start;
        public override int Read(byte[] buffer, int offset, int count)
        { if (count == 0) return 0; if (position == end) throw new InvalidDataException(); buffer[offset] = bytes[position++]; return 1; }
        public override int Read(Span<byte> buffer)
        { if (buffer.Length == 0) return 0; if (position == end) throw new InvalidDataException(); buffer[0] = bytes[position++]; return 1; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => end;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
