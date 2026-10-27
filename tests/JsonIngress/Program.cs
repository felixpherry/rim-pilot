using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimPilot.Json;

internal static class Program
{
    private static int passed;
    private static int failed;

    private static void Main(string[] args)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Native tests require Linux x64.");
        if (args.Length == 2 && args[0] == "native-fs-umask")
        {
            Environment.Exit(NativeFilesystemTests.RunUmaskChild(args[1]));
            return;
        }
        if (args.Length >= 1 && args.Length <= 2 && args[0] == "transport")
        {
            Environment.Exit(TransportTests.Run(args.Length == 2 ? args[1] : null) == 0 ? 0 : 1);
            return;
        }
        Test("same pinned net45 dependency under pure .NET tests (not Mono proof)", () =>
        {
            Assembly library = typeof(JToken).Assembly;
            Equal(".NETFramework,Version=v4.5", library.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
            True(library.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.StartsWith("13.0.3", StringComparison.Ordinal));
        });
        Test("upstream permissive controls (hardening is necessary)", () =>
        {
            Equal(2L, (long)JToken.Parse("{\"a\":1,\"a\":2}")["a"]!);
            JToken.Parse("{/*comment*/\"a\":1,}");
            True(double.IsNaN((double)JToken.Parse("NaN")));
        });

        foreach (string valid in new[]
        {
            "{}", "[]", "null", "true", "false", "0", "-0", "-1", "1.0", "1e2", "1E+2", "1e-2",
            "9223372036854775807", "-9223372036854775808", "1.7976931348623157e308", "5e-324",
            "\"雪 😀 café\"", "\"\\uD83D\\uDE00\"", "\"\\\"\\\\\\/\\b\\f\\n\\r\\t\"",
            "{\"a\":1,\"A\":2}", "{\"a\":{\"a\":2}}", "{\"\":0}", " \t\r\n[true,false,null,{}] \r\n"
        })
            Test("valid " + Label(valid), () => Parse(valid));

        var invalid = new Dictionary<string, string>
        {
            ["empty"] = "",
            ["whitespace"] = " \t\r\n",
            ["duplicate"] = "{\"a\":1,\"a\":2}",
            ["escaped duplicate"] = "{\"a\":1,\"\\u0061\":2}",
            ["nested duplicate"] = "{\"x\":{\"a\":0,\"a\":1}}",
            ["empty key duplicate"] = "{\"\":0,\"\":1}",
            ["line comment"] = "//comment\n{}",
            ["block comment"] = "{/*comment*/\"a\":1}",
            ["trailing comment"] = "{}/*comment*/",
            ["array comment"] = "[1,/*comment*/2]",
            ["object trailing comma"] = "{\"a\":1,}",
            ["array trailing comma"] = "[1,]",
            ["sparse array"] = "[1,,2]",
            ["NaN"] = "NaN",
            ["Infinity"] = "Infinity",
            ["negative Infinity"] = "-Infinity",
            ["nonfinite nested"] = "{\"n\":NaN}",
            ["undefined"] = "undefined",
            ["constructor"] = "new Date(0)",
            ["single quotes"] = "{'a':1}",
            ["unquoted key"] = "{a:1}",
            ["leading zero"] = "01",
            ["negative leading zero"] = "-01",
            ["hex"] = "0x12",
            ["plus"] = "+1",
            ["decimal missing integer"] = ".1",
            ["decimal missing fraction"] = "1.",
            ["exponent missing digits"] = "1e",
            ["exponent sign only"] = "1e+",
            ["minus only"] = "-",
            ["two roots"] = "{} []",
            ["adjacent roots"] = "truefalse",
            ["trailing garbage"] = "{}x",
            ["trailing nul"] = "{}\0",
            ["embedded nul"] = "\"a\0b\"",
            ["raw newline"] = "\"a\nb\"",
            ["bad escape"] = "\"\\x20\"",
            ["truncated escape"] = "\"\\u12\"",
            ["bad hex"] = "\"\\uZZZZ\"",
            ["unterminated string"] = "\"abc",
            ["high surrogate"] = "\"\\uD800\"",
            ["low surrogate"] = "\"\\uDC00\"",
            ["high surrogate then ordinary"] = "\"\\uD800a\"",
            ["two high surrogates"] = "\"\\uD800\\uD800\"",
            ["missing object close"] = "{\"a\":1",
            ["missing array close"] = "[1",
            ["missing colon"] = "{\"a\"1}",
            ["non JSON whitespace"] = "\u00a0{}",
            ["UTF8 BOM"] = "\ufeff{}"
        };
        foreach (var pair in invalid) Test("reject " + pair.Key, () => Reject(pair.Value, JsonFailure.Syntax));
        foreach (string number in new[] { "1e309", "-1e9999", "9223372036854775808", "-9223372036854775809" })
            Test("reject out-of-domain number " + number, () => Reject(number, JsonFailure.Number));

        foreach (byte[] bytes in new[]
        {
            new byte[] { 0xc0, 0xaf }, new byte[] { 0xe2, 0x82 }, new byte[] { 0xff }, new byte[] { 0x80 },
            new byte[] { 0x22, 0xed, 0xa0, 0x80, 0x22 }, new byte[] { 0xf4, 0x90, 0x80, 0x80 }
        }) Test("reject malformed UTF8 " + Convert.ToHexString(bytes), () => Reject(bytes, JsonFailure.InvalidUtf8));

        Test("byte quota inclusive", () => Parse(new string(' ', JsonLimits.MaxBytes - 1) + "0"));
        Test("byte quota exceeded", () => Reject(new string(' ', JsonLimits.MaxBytes) + "0", JsonFailure.Limit));
        Test("byte quota precedes decoding", () => Reject(Enumerable.Repeat((byte)0xff, JsonLimits.MaxBytes + 1).ToArray(), JsonFailure.Limit));
        Test("byte quota counts UTF8 not chars", () => Reject("\"" + new string('雪', JsonLimits.MaxBytes / 3 + 1) + "\"", JsonFailure.Limit));
        Test("depth quota inclusive", () => Parse(Nested(JsonLimits.MaxDepth)));
        Test("depth quota exceeded", () => Reject(Nested(JsonLimits.MaxDepth + 1), JsonFailure.Limit));
        Test("object depth quota exceeded", () => Reject(string.Concat(Enumerable.Repeat("{\"a\":", JsonLimits.MaxDepth + 1)) + "0" + new string('}', JsonLimits.MaxDepth + 1), JsonFailure.Limit));
        Test("string quota inclusive", () => Parse("\"" + new string('a', JsonLimits.MaxStringChars) + "\""));
        Test("string quota exceeded", () => Reject("\"" + new string('a', JsonLimits.MaxStringChars + 1) + "\"", JsonFailure.Limit));
        Test("decoded escaped string quota inclusive", () => Parse("\"" + string.Concat(Enumerable.Repeat("\\u0061", JsonLimits.MaxStringChars)) + "\""));
        Test("decoded escaped string quota exceeded", () => Reject("\"" + string.Concat(Enumerable.Repeat("\\u0061", JsonLimits.MaxStringChars + 1)) + "\"", JsonFailure.Limit));
        Test("supplementary string quota UTF16", () => Parse("\"" + string.Concat(Enumerable.Repeat("😀", JsonLimits.MaxStringChars / 2)) + "\""));
        Test("property name quota", () => Reject("{\"" + new string('a', JsonLimits.MaxStringChars + 1) + "\":0}", JsonFailure.Limit));
        Test("array quota inclusive", () => Parse(ArrayOf(JsonLimits.MaxArrayItems)));
        Test("array quota exceeded", () => Reject(ArrayOf(JsonLimits.MaxArrayItems + 1), JsonFailure.Limit));
        Test("object quota inclusive", () => Parse(ObjectOf(JsonLimits.MaxObjectProperties)));
        Test("object quota exceeded", () => Reject(ObjectOf(JsonLimits.MaxObjectProperties + 1), JsonFailure.Limit));
        Test("document node quota inclusive", () => Parse(NodeDocument(254)));
        Test("document node quota exceeded", () => Reject(NodeDocument(255), JsonFailure.Limit));
        Test("numeric length quota inclusive", () => Parse("0." + new string('1', JsonLimits.MaxNumberChars - 2)));
        Test("numeric length quota exceeded", () => Reject("0." + new string('1', JsonLimits.MaxNumberChars - 1), JsonFailure.Limit));
        Test("long integer lexeme bounded before parse", () => Reject(new string('1', JsonLimits.MaxNumberChars + 1), JsonFailure.Limit));

        Test("global serializer settings are never invoked", () =>
        {
            var previous = JsonConvert.DefaultSettings;
            try
            {
                JsonConvert.DefaultSettings = () => throw new Exception("Serializer invoked");
                Map(Parse("{\"label\":\"ok\",\"count\":2,\"enabled\":true}"));
            }
            finally { JsonConvert.DefaultSettings = previous; }
        });
        Test("dates remain literal strings", () => Equal(JTokenType.String, Parse("\"2026-10-26T00:00:00Z\"").Type));
        Test("culture independent parse and mapping", () =>
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); Equal(1.5, (double)Parse("1.5")); }
            finally { CultureInfo.CurrentCulture = previous; }
        });
        Test("explicit primitive DTO mapping preserves Unicode", () =>
        {
            var dto = Map(Parse("{\"label\":\"雪 😀\",\"count\":2,\"enabled\":true}"));
            Equal("雪 😀", dto.Label); Equal(2L, dto.Count); True(dto.Enabled);
        });
        foreach (string input in new[]
        {
            "{\"label\":1,\"count\":2,\"enabled\":true}", "{\"label\":null,\"count\":2,\"enabled\":true}",
            "{\"label\":\"ok\",\"count\":\"2\",\"enabled\":true}", "{\"label\":\"ok\",\"count\":2.0,\"enabled\":true}",
            "{\"label\":\"ok\",\"count\":2,\"enabled\":\"true\"}", "{\"label\":\"ok\",\"count\":-1,\"enabled\":true}",
            "{\"label\":\"ok\",\"count\":11,\"enabled\":true}", "{\"label\":\"ok\",\"count\":2}",
            "{\"Label\":\"ok\",\"count\":2,\"enabled\":true}", "{\"label\":\"ok\",\"count\":2,\"enabled\":true,\"extra\":0}",
            "{\"$type\":\"System.IO.FileInfo, mscorlib\",\"label\":\"ok\",\"count\":2,\"enabled\":true}",
            "[]", "null", "{\"label\":\"too long label\",\"count\":2,\"enabled\":true}"
        }) Test("DTO rejects without coercion " + Label(input), () => ExpectFailure(() => Map(Parse(input)), JsonFailure.Field));
        Test("metadata is inert data, never type resolution", () =>
        {
            JToken token = Parse("{\"$type\":\"No.Such.Type, No.Such.Assembly\",\"$values\":[1]}");
            Equal(JTokenType.Object, token.Type);
            Equal("No.Such.Type, No.Such.Assembly", (string)token["$type"]!);
            Equal(1, JsonFields.Array(token["$values"]!, 1).Count);
        });
        Test("nested DTOs must opt in to fields", () =>
        {
            var fields = new JsonFields(Parse("{\"outer\":{\"$type\":\"x\"}}"), "outer");
            ExpectFailure(() => new JsonFields(fields.Required("outer"), "label"), JsonFailure.Field);
        });
        Test("array DTO rejects scalar", () => ExpectFailure(() => JsonFields.Array(Parse("0"), 2), JsonFailure.Field));
        Test("array DTO enforces tighter count", () => ExpectFailure(() => JsonFields.Array(Parse("[1,2]"), 1), JsonFailure.Field));
        Test("missing differs from explicit null", () =>
        {
            var fields = new JsonFields(Parse("{\"x\":null}"), "x", "y");
            True(fields.Has("x")); True(!fields.Has("y")); Equal(JTokenType.Null, fields.Required("x").Type);
        });
        Test("failures retain no credential or input detail", () =>
        {
            const string secret = "secret-credential-value";
            try { Parse("{\"" + secret + "\":0,\"" + secret + "\":1}"); throw new Exception("Accepted duplicate"); }
            catch (StrictJsonException error)
            {
                Equal("JSON input rejected.", error.Message); True(error.InnerException == null);
                True(error.Data.Count == 0); True(!error.ToString().Contains(secret, StringComparison.Ordinal));
            }
        });
        Console.WriteLine($"JSON ingress: {passed} passed; {failed} failed. .NET tests only; Unity Mono loading deferred.");
        int wireFailed = WireTests.Run();
        int safetyFailed = ObservationSafetyTests.Run();
        int discoveryFailed = DiscoveryTests.Run();
        int nativeFailed = NativeFilesystemTests.Run();
        int transportFailed = TransportTests.Run();
        int sessionsFailed = SessionDiscoveryTests.Run();
        int lifecycleFailed = ProcessLifecycleTests.Run();
        int dispatcherFailed = ReadDispatcherTests.Run();
        if (failed != 0 || wireFailed != 0 || safetyFailed != 0 || discoveryFailed != 0 || nativeFailed != 0 || transportFailed != 0 || sessionsFailed != 0 || lifecycleFailed != 0 || dispatcherFailed != 0) Environment.Exit(1);
    }

    private static JToken Parse(string text) { return StrictJson.Parse(Encoding.UTF8.GetBytes(text)); }
    private static string Nested(int depth) { return new string('[', depth) + "0" + new string(']', depth); }
    private static string ArrayOf(int count) { return "[" + string.Join(",", Enumerable.Repeat("null", count)) + "]"; }
    private static string ObjectOf(int count) { return "{" + string.Join(",", Enumerable.Range(0, count).Select(i => "\"k" + i + "\":0")) + "}"; }
    private static string NodeDocument(int finalCount)
    {
        // 1 root + 15*(1 array + 255 nulls) + (1 array + finalCount nulls).
        return "[" + string.Join(",", Enumerable.Repeat(ArrayOf(255), 15).Concat(new[] { ArrayOf(finalCount) })) + "]";
    }
    private static string Label(string text) { return text.Length > 60 ? text.Substring(0, 60) : text; }
    private static void Reject(string text, JsonFailure failure) { Reject(Encoding.UTF8.GetBytes(text), failure); }
    private static void Reject(byte[] bytes, JsonFailure failure) { ExpectFailure(() => StrictJson.Parse(bytes), failure); }
    private static void ExpectFailure(Action action, JsonFailure failure)
    {
        try { action(); }
        catch (StrictJsonException error) { Equal(failure, error.Failure); return; }
        throw new Exception("Expected rejection: " + failure);
    }
    private static void Test(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
    }
    private static void True(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static ProbeDto Map(JToken token)
    {
        var fields = new JsonFields(token, "label", "count", "enabled");
        return new ProbeDto(fields.String("label", 8), fields.Integer("count", 0, 10), fields.Boolean("enabled"));
    }
    private sealed class ProbeDto
    {
        internal string Label { get; }
        internal long Count { get; }
        internal bool Enabled { get; }
        internal ProbeDto(string label, long count, bool enabled) { Label = label; Count = count; Enabled = enabled; }
    }
}
