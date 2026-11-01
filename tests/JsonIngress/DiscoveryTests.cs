using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimPilot.Protocol;

internal static class DiscoveryTests
{
    private static int passed;
    private static int failed;
    private const string Session = "s_11111111111111111111111111111111";
    private const string Token = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string Name = Session + ".json";
    private static JObject Record() => new JObject { ["v"] = 1, ["session"] = Session,
        ["host"] = "127.0.0.1", ["port"] = 12345, ["token"] = Token };
    private static byte[] Bytes(JToken value) => Encoding.UTF8.GetBytes(value.ToString(Formatting.None));
    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);
    private static DiscoveryDescriptor Read(JObject value) => DiscoveryDescriptor.Read(Bytes(value), Name);

    internal static int Run()
    {
        JObject fixtures = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "discovery.json")));
        foreach (JObject value in (JArray)fixtures["accepted"]!)
            Test("shared descriptor accept fixture", () => DiscoveryDescriptor.Read(Bytes(value), (string)value["session"]! + ".json"));
        foreach (JObject value in (JArray)fixtures["rejected"]!)
            Test("shared descriptor reject fixture", () => Invalid(value));
        Test("private descriptor roundtrip and public-only listing", () =>
        {
            var original = new DiscoveryDescriptor(Session, 12345, Token);
            var decoded = DiscoveryDescriptor.Read(original.EncodePrivate(), Name);
            Equal(Session, decoded.Session); Equal(12345, decoded.Port); Equal(Token, decoded.Token);
            Equal(Name, decoded.RecordName);
            Equal("session,v", string.Join(",", decoded.PublicJson().Properties().Select(p => p.Name).OrderBy(p => p)));
            True(!decoded.PublicJson().ToString().Contains(Token, StringComparison.Ordinal));
            True(!decoded.ToString().Contains(Token, StringComparison.Ordinal));
        });
        foreach (string field in new[] { "v", "session", "host", "port", "token" })
        {
            Test("required descriptor field " + field, () => { JObject v = Record(); v.Remove(field); Invalid(v); });
            Test("descriptor null rejected " + field, () => { JObject v = Record(); v[field] = null; Invalid(v); });
            Test("descriptor field no coercion " + field, () =>
            {
                JObject v = Record(); v[field] = field == "port" || field == "v" ? new JValue("1") : new JValue(1); Invalid(v);
            });
        }
        foreach (string field in new[] { "extra", "$type", "pid", "path", "credentialPath", "endpoint", "Host" })
            Test("unknown descriptor field " + field, () => { JObject v = Record(); v[field] = "not-public"; Invalid(v); });
        foreach (string host in new[] { "localhost", "127.1", "127.0.0.2", "127.000.000.001", "2130706433",
            "0x7f000001", "::1", "[::1]", "::ffff:127.0.0.1", "0.0.0.0", "192.0.2.1", "http://127.0.0.1",
            "127.0.0.1:12345", "127.0.0.1 ", "127.0.0.1\0", "", "１２７.0.0.1" })
            Test("literal loopback only " + host.Length, () => { JObject v = Record(); v["host"] = host; Invalid(v); });
        foreach (JToken port in new JToken[] { 0, -1, 65536, long.MaxValue, 1.0, true, "12345" })
            Test("port domain strict " + port.Type + port, () => { JObject v = Record(); v["port"] = port; Invalid(v); });
        foreach (int port in new[] { 1, 65535 })
            Test("port inclusive " + port, () => { JObject v = Record(); v["port"] = port; Equal(port, Read(v).Port); });
        foreach (JToken version in new JToken[] { 0, 2, 1.0, true, "1" })
            Test("version exact " + version.Type + version, () => { JObject v = Record(); v["v"] = version; Invalid(v); });
        foreach (string session in new[] { "s_1", Session.ToUpperInvariant(), "p_" + Session.Substring(2),
            Session + "\n", Session + "x", "s_" + new string('z', 32), "../" + Session })
            Test("session exact " + session.Length, () => { JObject v = Record(); v["session"] = session; Invalid(v); });
        foreach (string token in new[] { "", new string('a', 63), new string('a', 65), new string('A', 64), new string('g', 64), Token + "\n" })
            Test("token exact " + token.Length, () => { JObject v = Record(); v["token"] = token; Invalid(v); });
        foreach (string filename in new[] { "../" + Name, "/private/" + Name, Name.ToUpperInvariant(), Name + ".tmp", "other.json", "s_33333333333333333333333333333333.json", "", Name + "\0" })
            Test("basename matches descriptor " + filename.Length, () => Reject(() => DiscoveryDescriptor.Read(Bytes(Record()), filename), DiscoveryError.InvalidDescriptor));
        foreach (string invalid in new[] { "[]", "null", "{}", "", "{} {}", "{/*secret*/}", "{\"v\":1,}",
            "{\"v\":1,\"v\":1}", "{\"token\":\"private-secret\",\"tok\\u0065n\":0}", "NaN", "\ufeff{}" })
            Test("strict descriptor grammar " + invalid.Length, () => Reject(() => DiscoveryDescriptor.Read(Text(invalid), Name), DiscoveryError.InvalidDescriptor));
        Test("descriptor UTF8 rejection", () => Reject(() => DiscoveryDescriptor.Read(new byte[] { 0xff }, Name), DiscoveryError.InvalidDescriptor));
        Test("descriptor byte ceiling inclusive", () =>
        {
            byte[] record = Bytes(Record()); var padded = new byte[DiscoveryLimits.DescriptorBytes];
            Array.Fill(padded, (byte)' '); record.CopyTo(padded, 0);
            Equal(Session, DiscoveryDescriptor.Read(padded, Name).Session);
            Reject(() => DiscoveryDescriptor.Read(padded.Concat(new byte[] { 32 }).ToArray(), Name), DiscoveryError.InvalidDescriptor);
        });
        Test("descriptor byte cap precedes UTF8 and larger wire profile", () =>
            Reject(() => DiscoveryDescriptor.Read(Enumerable.Repeat((byte)0xff, 1025).ToArray(), Name), DiscoveryError.InvalidDescriptor));
        Test("constructor validates before private encoding", () =>
        {
            Reject(() => new DiscoveryDescriptor(Session, 0, Token), DiscoveryError.InvalidDescriptor);
            Reject(() => new DiscoveryDescriptor("secret-invalid-session", 1, Token), DiscoveryError.InvalidDescriptor);
            Reject(() => new DiscoveryDescriptor(Session, 1, "private-secret"), DiscoveryError.InvalidDescriptor);
        });
        Test("global serializer defaults never run", () =>
        {
            var previous = JsonConvert.DefaultSettings;
            try
            {
                JsonConvert.DefaultSettings = () => throw new Exception("Serializer invoked");
                DiscoveryDescriptor.Read(new DiscoveryDescriptor(Session, 12345, Token).EncodePrivate(), Name);
            }
            finally { JsonConvert.DefaultSettings = previous; }
        });
        Test("finite record/entry/probe budget boundaries", () =>
        {
            var entries = new DiscoveryBudget(); for (int i = 0; i < 64; i++) entries.ExamineEntry();
            Reject(entries.ExamineEntry, DiscoveryError.LimitExceeded);
            Reject(entries.ReadRecord, DiscoveryError.LimitExceeded);
            var records = new DiscoveryBudget(); for (int i = 0; i < 16; i++) records.ReadRecord();
            Reject(records.ReadRecord, DiscoveryError.LimitExceeded);
            var probes = new DiscoveryBudget();
            for (int i = 0; i < 16; i++) Equal(1000, probes.BeginProbe(i));
            Reject(() => probes.BeginProbe(16), DiscoveryError.LimitExceeded);
            Equal(DiscoveryLimits.Records * DiscoveryLimits.DescriptorBytes, DiscoveryLimits.TotalRecordBytes);
            Equal(1, DiscoveryLimits.ConcurrentProbes);
        });
        Test("absolute probe allowance and nonmonotonic refusal", () =>
        {
            var budget = new DiscoveryBudget(); Equal(1000, budget.BeginProbe(0));
            Equal(1, budget.BeginProbe(4999)); Reject(() => budget.BeginProbe(5000), DiscoveryError.LimitExceeded);
            Reject(() => new DiscoveryBudget().BeginProbe(-1), DiscoveryError.LimitExceeded);
            var backwards = new DiscoveryBudget(); backwards.BeginProbe(10);
            Reject(() => backwards.BeginProbe(9), DiscoveryError.LimitExceeded);
            Reject(() => backwards.BeginProbe(11), DiscoveryError.LimitExceeded);
            Reject(() => new DiscoveryBudget().BeginProbe(long.MaxValue), DiscoveryError.LimitExceeded);
        });
        Test("verified XDG takes precedence and is sole selected registry", () =>
        {
            var calls = new List<RuntimeDirectory>();
            RuntimeDirectory result = RuntimeDirectoryPolicy.Select("/run/user/1234", 1234, p => { calls.Add(p); return true; });
            Equal(1, calls.Count); Equal("/run/user/1234", result.BasePath);
            Equal("/run/user/1234/rim-pilot/v1", result.RegistryPath); True(!result.IsFallback);
        });
        Test("unsafe XDG falls back to fixed effective-UID path", () =>
        {
            var calls = new List<RuntimeDirectory>();
            RuntimeDirectory result = RuntimeDirectoryPolicy.Select("/unsafe/private", 1234, p => { calls.Add(p); return p.IsFallback; });
            Equal(2, calls.Count); True(result.IsFallback); Equal("/tmp/rim-pilot-1234", result.BasePath);
            Equal("/tmp/rim-pilot-1234/v1", result.RegistryPath);
            True(!result.ToString().Contains(result.BasePath, StringComparison.Ordinal));
        });
        foreach (string? invalid in new[] { null, "", "/", "relative", "~/runtime", "$RUNTIME", "/a/", "/a//b", "/a/./b", "/a/../b",
            "/bad\npath", "/bad\0path", "/bad\\path", "/" + new string('x', 256), "/" + new string('雪', 86), "/bad\ud800", "/" + new string('x', 1024) })
            Test("malformed XDG skips native primary " + (invalid?.Length ?? -1), () =>
            {
                int calls = 0;
                RuntimeDirectory result = RuntimeDirectoryPolicy.Select(invalid, 42, p => { calls++; True(p.IsFallback); return true; });
                Equal(1, calls); Equal("/tmp/rim-pilot-42/v1", result.RegistryPath);
            });
        Test("Unicode XDG preserved no normalization", () =>
        {
            RuntimeDirectory result = RuntimeDirectoryPolicy.Select("/private/雪 😀", 42, _ => true);
            Equal("/private/雪 😀/rim-pilot/v1", result.RegistryPath);
        });
        Test("runtime path ceiling inclusive", () =>
        {
            string path = "/" + new string('a', 255) + "/" + new string('b', 255) + "/" + new string('c', 255) + "/" + new string('d', 255);
            Equal(1024, path.Length); True(!RuntimeDirectoryPolicy.Select(path, 42, _ => true).IsFallback);
        });
        Test("fixed fallback ignores ambient HOME TMPDIR and culture", () =>
        {
            CultureInfo old = CultureInfo.CurrentCulture;
            string? home = Environment.GetEnvironmentVariable("HOME"), temp = Environment.GetEnvironmentVariable("TMPDIR");
            try
            {
                Environment.SetEnvironmentVariable("HOME", "/private-secret-home"); Environment.SetEnvironmentVariable("TMPDIR", "/private-secret-temp");
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                Equal("/tmp/rim-pilot-4294967295/v1", RuntimeDirectoryPolicy.Select(null, uint.MaxValue, _ => true).RegistryPath);
            }
            finally { CultureInfo.CurrentCulture = old; Environment.SetEnvironmentVariable("HOME", home); Environment.SetEnvironmentVariable("TMPDIR", temp); }
        });
        Test("unverified fallback refuses with no path or inner diagnostic", () =>
            Reject(() => RuntimeDirectoryPolicy.Select("/private-secret", 42, _ => false), DiscoveryError.RuntimeUnavailable));
        foreach (Exception error in new Exception[] { new IOException("/private-secret/" + Token),
            new UnauthorizedAccessException(Token), new System.Security.SecurityException(Token), new DiscoveryException(DiscoveryError.RuntimeUnavailable) })
            Test("native refusal diagnostics discarded " + error.GetType().Name, () =>
                Reject(() => RuntimeDirectoryPolicy.Select("/private-secret", 42, _ => throw error), DiscoveryError.RuntimeUnavailable));
        Console.WriteLine($"Discovery contracts: {passed} passed; {failed} failed. Descriptor/policy tests only; listener/probes and Mono/game deferred.");
        return failed;
    }

    private static void Invalid(JObject value) => Reject(() => Read(value), DiscoveryError.InvalidDescriptor);
    private static void Reject(Action action, DiscoveryError code)
    {
        try { action(); }
        catch (DiscoveryException error)
        {
            Equal(code, error.Code); Equal("Session discovery rejected.", error.Message);
            True(error.InnerException == null); Equal(0, error.Data.Count);
            True(!error.ToString().Contains(Token, StringComparison.Ordinal));
            True(!error.ToString().Contains("private-secret", StringComparison.Ordinal)); return;
        }
        throw new Exception("Expected discovery rejection");
    }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS discovery " + name); }
        catch (Exception error) { failed++; Console.Error.WriteLine("FAIL discovery " + name + ": " + error); }
    }
    private static void True(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Values differ"); }
}
