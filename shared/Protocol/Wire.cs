using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimPilot.Json;

namespace RimPilot.Protocol
{
    internal static class WireLimits
    {
        internal const int Version = 1;
        internal const int RequestBytes = 65536;
        internal const int ResponseBytes = 262144;
        internal const int CaptureResponseBytes = 4194304;
        internal const int CapturePngBytes = 3141632;
        internal const int CaptureChunkChars = 16384;
        internal const int MaxConnections = 4;
        internal const int ListenBacklog = 4;
        internal const int AdmissionsPerSecond = 8;
        internal const int AdmissionBurst = 16;
        internal const int AdmissionRetryMs = 125;
        internal const int SocketBufferBytes = 16384;
        internal const int SocketWatchdogMs = 25;
        internal const int ShutdownTimeoutMs = 1000;
        internal const int RequestBytesPerSecond = (RequestBytes + 4) * RequestsPerSecond;
        internal const int RequestByteBurst = (RequestBytes + 4) * RequestBurst;
        internal const int ResponseBytesPerSecond = CaptureResponseBytes + 4;
        internal const int ResponseByteBurst = CaptureResponseBytes + 4;
        internal const int PendingRequests = 16;
        internal const int ReadsPerUpdate = 4;
        internal const int TrackedMaps = 128;
        // Eligible-only pawn/thing references per generation: four full retained snapshots.
        internal const int TrackedEntities = Snapshots * SnapshotItems;
        internal const int RequestsPerSecond = 8;
        internal const int RequestBurst = 16;
        internal const int AuthTimeoutMs = 2000;
        internal const int RequestTimeoutMs = 3000;
        internal const int WriteTimeoutMs = 2000;
        internal const int IdleTimeoutMs = 10000;
        internal const int PageItems = 64;
        internal const int SnapshotItems = 2048;
        internal const int SnapshotBytes = 2097152;
        internal const int Snapshots = 4;
        internal const int ActiveSnapshotBuilds = 1;
        internal const int SnapshotLifetimeMs = 5000;
        internal const int ScanCandidates = 16384;
        internal const int CandidatesPerUpdate = 128;
        internal const int WorkPerUpdateMs = 2;
        // Session-wide producer ownership. Retained snapshots are budgeted separately.
        internal const int ActiveReads = 4;
        internal const int NestedFieldsPerUpdate = 1024;
        internal const int NestedFieldsPerRead = 65536;
        internal const int ActiveWorkBytes = SnapshotBytes;
        internal const int EventItems = 256;
        internal const int EventBytes = 262144;
        internal const int EventCursors = 128;
        internal const int EventCursorLifetimeMs = 5000;
        internal const int CaptureWidth = 1920;
        internal const int CaptureHeight = 1080;
        internal const int CapturePixels = 2073600;
        internal const int CaptureIntervalMs = 1000;
    }

    internal enum WireError
    {
        InvalidFrame, InvalidJson, InvalidRequest, VersionMismatch, AuthenticationFailed,
        ForbiddenMethod, UnsupportedMethod, NotReady, ObservationRefused, Unavailable,
        StaleScope, CursorExpired, EventGap, Busy, RateLimited, Timeout, ResponseTooLarge, InternalError
    }

    internal sealed class WireException : Exception
    {
        internal WireError Code { get; }
        internal WireException(WireError code) : base("Bridge request rejected.") { Code = code; }
    }

    internal static class Framing
    {
        // UInt32 big-endian byte length, excluding header. Never allocate from an unchecked length.
        // Caller passes a linked, finite deadline token and closes connection on failure. Cancellation
        // support depends on Stream implementation; socket owner must close on deadline as backstop.
        internal static async Task<byte[]> ReadAsync(Stream stream, int maxBytes, CancellationToken deadline,
            byte? firstHeaderByte = null, Action<int>? reserveBytes = null)
        {
            CheckCeiling(maxBytes);
            var header = new byte[4];
            if (firstHeaderByte.HasValue) header[0] = firstHeaderByte.Value;
            await ExactAsync(stream, header, deadline, firstHeaderByte.HasValue ? 1 : 0).ConfigureAwait(false);
            uint length = ((uint)header[0] << 24) | ((uint)header[1] << 16) | ((uint)header[2] << 8) | header[3];
            if (length == 0 || length > maxBytes) throw new WireException(WireError.InvalidFrame);
            // Trusted transport quota callback, after length validation but before allocation/read.
            reserveBytes?.Invoke((int)length + 4);
            var body = new byte[(int)length];
            await ExactAsync(stream, body, deadline).ConfigureAwait(false);
            return body;
        }

        internal static async Task WriteAsync(Stream stream, byte[] body, int maxBytes, CancellationToken deadline)
        {
            CheckCeiling(maxBytes);
            if (body == null || body.Length == 0 || body.Length > maxBytes) throw new WireException(WireError.InvalidFrame);
            int length = body.Length;
            var header = new[] { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length };
            deadline.ThrowIfCancellationRequested();
            await stream.WriteAsync(header, 0, header.Length, deadline).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, deadline).ConfigureAwait(false);
        }

        private static async Task ExactAsync(Stream stream, byte[] buffer, CancellationToken deadline, int offset = 0)
        {
            while (offset < buffer.Length)
            {
                deadline.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, deadline).ConfigureAwait(false);
                if (read == 0) throw new WireException(WireError.InvalidFrame);
                offset += read;
            }
        }
        private static void CheckCeiling(int maxBytes)
        {
            if (maxBytes != WireLimits.RequestBytes && maxBytes != WireLimits.ResponseBytes && maxBytes != WireLimits.CaptureResponseBytes)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
    }

    internal static class WireJson
    {
        internal static byte[] EncodeResponse(string id, ReadMethod method, JObject data)
        {
            byte[] bytes = Encode(Responses.Success(id, data), method == ReadMethod.Screenshot);
            ResponseContracts.Read(bytes, method, id);
            return bytes;
        }

        // Hand-built JObjects of approved primitives only; no object serializer, converters or defaults.
        // Validate complete response before sending. Budgeted stream refuses growth before oversized writes.
        internal static byte[] Encode(JObject value, bool capture = false)
        {
            int max = capture ? WireLimits.CaptureResponseBytes : WireLimits.ResponseBytes;
            using (var bytes = new BudgetStream(max))
            {
                using (var text = new StreamWriter(bytes, new UTF8Encoding(false, true), 1024, true))
                using (var writer = new JsonTextWriter(text) { CloseOutput = false, Formatting = Formatting.None })
                { value.WriteTo(writer); }
                byte[] result = bytes.ToArray();
                try { StrictJson.ParseResponse(result, capture); }
                catch (StrictJsonException) { throw new WireException(WireError.ResponseTooLarge); }
                return result;
            }
        }
        private sealed class BudgetStream : MemoryStream
        {
            private readonly int limit;
            internal BudgetStream(int limit) { this.limit = limit; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count > limit - Length) throw new WireException(WireError.ResponseTooLarge);
                base.Write(buffer, offset, count);
            }
            public override void WriteByte(byte value)
            {
                if (Length == limit) throw new WireException(WireError.ResponseTooLarge);
                base.WriteByte(value);
            }
        }
    }
}
