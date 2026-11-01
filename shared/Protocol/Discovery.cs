using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using Newtonsoft.Json.Linq;
using RimPilot.Json;

namespace RimPilot.Protocol
{
    internal static class DiscoveryLimits
    {
        internal const int DescriptorBytes = 1024;
        internal const int DirectoryEntries = 64;
        internal const int Records = 16;
        internal const int TotalRecordBytes = Records * DescriptorBytes;
        internal const int Probes = 16;
        internal const int ConcurrentProbes = 1;
        internal const int ProbeTimeoutMs = 1000;
        internal const int TotalProbeTimeoutMs = 5000;
        internal const int RuntimePathChars = 1024;
    }

    internal enum DiscoveryError { InvalidDescriptor, LimitExceeded, RuntimeUnavailable }

    internal sealed class DiscoveryException : Exception
    {
        internal DiscoveryError Code { get; }
        internal DiscoveryException(DiscoveryError code) : base("Session discovery rejected.") { Code = code; }
    }

    // Private on-disk material, never a CLI response or log object. No PID/path/hostname hints.
    internal sealed class DiscoveryDescriptor
    {
        internal const string Loopback = "127.0.0.1";
        internal string Session { get; }
        internal int Port { get; }
        internal string Token { get; }
        internal string RecordName => Session + ".json";

        internal DiscoveryDescriptor(string session, int port, string token)
        {
            if (!Ids.Is(session, "s") || port < 1 || port > 65535 || !Ids.IsHex(token, 64)) Reject();
            Session = session; Port = port; Token = token;
        }

        // Native reader must cap allocation/read at DescriptorBytes + one overflow sentinel first.
        // Filename is one basename from fd-relative enumeration, never a client-supplied path.
        internal static DiscoveryDescriptor Read(byte[] bytes, string recordName)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > DiscoveryLimits.DescriptorBytes) Reject();
            try
            {
                var fields = new JsonFields(StrictJson.Parse(bytes!), "v", "session", "host", "port", "token");
                if (fields.Integer("v", 1, 1) != WireLimits.Version || fields.String("host", 15) != Loopback) Reject();
                var result = new DiscoveryDescriptor(fields.String("session", 34),
                    (int)fields.Integer("port", 1, 65535), fields.String("token", 64));
                if (!string.Equals(recordName, result.RecordName, StringComparison.Ordinal)) Reject();
                return result;
            }
            catch (StrictJsonException) { throw new DiscoveryException(DiscoveryError.InvalidDescriptor); }
        }

        internal byte[] EncodePrivate()
        {
            byte[] bytes = WireJson.Encode(new JObject { ["v"] = WireLimits.Version, ["session"] = Session,
                ["host"] = Loopback, ["port"] = Port, ["token"] = Token });
            if (bytes.Length > DiscoveryLimits.DescriptorBytes) Reject();
            return bytes;
        }

        // Ordinary listing exposes only public targeting identity/protocol, not credential or path.
        internal JObject PublicJson() => new JObject { ["v"] = WireLimits.Version, ["session"] = Session };
        public override string ToString() => "Private session descriptor.";
        private static void Reject() { throw new DiscoveryException(DiscoveryError.InvalidDescriptor); }
    }

    // Reservations precede directory iteration, record reads and probes, including failed attempts.
    // Exhaustion aborts the whole discovery; never return a prefix that could hide ambiguity.
    internal sealed class DiscoveryBudget
    {
        private int entries;
        private int records;
        private int probes;
        private long lastElapsedMs;
        private bool exhausted;
        internal void ExamineEntry() { Reserve(ref entries, DiscoveryLimits.DirectoryEntries); }
        internal void ReadRecord() { Reserve(ref records, DiscoveryLimits.Records); }
        // Caller supplies elapsed time from ONE monotonic discovery clock, never wall-clock time.
        // Socket owner enforces returned absolute allowance with socket-close backstop.
        internal int BeginProbe(long elapsedMs)
        {
            if (elapsedMs < lastElapsedMs || elapsedMs < 0 || elapsedMs >= DiscoveryLimits.TotalProbeTimeoutMs) Fail();
            lastElapsedMs = elapsedMs;
            Reserve(ref probes, DiscoveryLimits.Probes);
            return (int)Math.Min(DiscoveryLimits.ProbeTimeoutMs, DiscoveryLimits.TotalProbeTimeoutMs - elapsedMs);
        }
        private void Reserve(ref int used, int maximum)
        {
            if (exhausted || used == maximum) Fail();
            used++;
        }
        private void Fail()
        {
            exhausted = true;
            throw new DiscoveryException(DiscoveryError.LimitExceeded);
        }
    }

    internal sealed class RuntimeDirectory
    {
        internal string BasePath { get; }
        internal string RegistryPath { get; }
        internal bool IsFallback { get; }
        internal RuntimeDirectory(string basePath, string registryPath, bool fallback)
        { BasePath = basePath; RegistryPath = registryPath; IsFallback = fallback; }
        public override string ToString() => "Private runtime directory.";
    }

    internal static class RuntimeDirectoryPolicy
    {
        // Both endpoints link this source. uid MUST be native geteuid(), not env/username/PID.
        // Verifier MUST use the native no-follow fd boundary, not Directory.Exists/managed modes.
        // This policy does no filesystem access and establishes no ownership/permission guarantee.
        internal static RuntimeDirectory Select(string? xdgRuntimeDirectory, uint uid, Func<RuntimeDirectory, bool> verifyOrCreate)
        {
            if (verifyOrCreate == null) throw new DiscoveryException(DiscoveryError.RuntimeUnavailable);
            if (ValidBase(xdgRuntimeDirectory))
            {
                var primary = new RuntimeDirectory(xdgRuntimeDirectory!, xdgRuntimeDirectory + "/rim-pilot/v1", false);
                if (Verify(primary, verifyOrCreate)) return primary;
            }
            string fallback = "/tmp/rim-pilot-" + uid.ToString(CultureInfo.InvariantCulture);
            var candidate = new RuntimeDirectory(fallback, fallback + "/v1", true);
            if (Verify(candidate, verifyOrCreate)) return candidate;
            throw new DiscoveryException(DiscoveryError.RuntimeUnavailable);
        }

        private static bool Verify(RuntimeDirectory candidate, Func<RuntimeDirectory, bool> verifier)
        {
            try { return verifier(candidate); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (SecurityException) { return false; }
            catch (DiscoveryException) { return false; }
        }

        private static bool ValidBase(string? path)
        {
            if (string.IsNullOrEmpty(path) || path!.Length > DiscoveryLimits.RuntimePathChars
                || path[0] != '/' || path.Length == 1 || path[path.Length - 1] == '/') return false;
            foreach (char c in path) if (char.IsControl(c) || c == '\\') return false;
            foreach (string part in path.Substring(1).Split('/'))
            {
                if (part.Length == 0 || part == "." || part == "..") return false;
                try { if (new UTF8Encoding(false, true).GetByteCount(part) > 255) return false; }
                catch (EncoderFallbackException) { return false; }
            }
            return true;
        }
    }
}
