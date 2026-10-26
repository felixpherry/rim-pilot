using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimPilot.Json;
using RimPilot.Protocol;

internal static class WireTests
{
    private static int passed;
    private static int failed;
    private const string Session = "s_11111111111111111111111111111111";
    private const string Root = "r_22222222222222222222222222222222";
    private const string Token = "0000000000000000000000000000000000000000000000000000000000000000";
    private static Scope MapScope() => new Scope(Session, Root, "g_33333333333333333333333333333333", "m_44444444444444444444444444444444");
    private static byte[] Bytes(JToken value) => Encoding.UTF8.GetBytes(value.ToString(Formatting.None));
    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);
    private static JObject Fixture(string name) => (JObject)StrictJson.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name)));
    private static JObject Auth() => new JObject { ["v"] = 1, ["id"] = "a", ["method"] = "auth", ["params"] = new JObject { ["session"] = Session, ["token"] = Token } };

    internal static int Run()
    {
        JObject requests = Fixture("requests.json");
        foreach (JToken request in (JArray)requests["accepted"]!)
            Test("request fixture " + request["method"], () => Request.Read(Bytes(request)));
        foreach (JToken entry in (JArray)requests["rejected"]!)
            Test("request reject fixture " + entry["error"] + "/" + entry["request"]!["method"], () =>
                Reject(() => Request.Read(Bytes(entry["request"]!)), Enum.Parse<WireError>((string)entry["error"]!)));
        foreach (string method in new[] { "click", "key", "text", "scroll", "drag", "pause", "select", "camera", "save", "load", "debug", "eval", "action", "grant", "reflect" })
            Test("no action method " + method, () => Reject(() => Request.Read(Text("{\"v\":1,\"id\":\"x\",\"method\":\"" + method + "\",\"params\":{}}")), WireError.ForbiddenMethod));
        foreach (JToken entry in (JArray)requests["accepted"]!)
        {
            JObject altered = (JObject)entry.DeepClone(); altered["params"]!["extra"] = 1;
            Test("no extra params " + entry["method"], () => Reject(() => Request.Read(Bytes(altered)), WireError.InvalidRequest));
        }
        foreach (JToken entry in (JArray)requests["rejected"]!)
            Test("uniform pre-auth failure " + entry["error"] + "/" + entry["request"]!["method"], () =>
            { var auth = new Authentication(Session, Token); Reject(() => auth.Accept(Bytes(entry["request"]!)), WireError.AuthenticationFailed); True(!auth.Authenticated); });
        Test("auth successful exact session and token", () => { var auth = new Authentication(Session, Token); auth.Accept(Bytes(Auth())); True(auth.Authenticated); Reject(() => auth.Accept(Bytes(Auth())), WireError.AuthenticationFailed); });
        Test("wrong session with right token refused", () => { JObject auth = Auth(); auth["params"]!["session"] = Ids.New("s"); Reject(() => new Authentication(Session, Token).Accept(Bytes(auth)), WireError.AuthenticationFailed); });
        foreach (int index in new[] { 0, 31, 63 })
            Test("wrong token position " + index, () => { JObject auth = Auth(); var chars = Token.ToCharArray(); chars[index] = '1'; auth["params"]!["token"] = new string(chars); Reject(() => new Authentication(Session, Token).Accept(Bytes(auth)), WireError.AuthenticationFailed); });
        Test("failure terminal for connection", () => { var auth = new Authentication(Session, Token); Reject(() => auth.Accept(Text("{}")), WireError.AuthenticationFailed); Reject(() => auth.Accept(Bytes(Auth())), WireError.AuthenticationFailed); });
        foreach (string invalid in new[] { "not json", "{\"token\":\"sensitive-do-not-echo\",\"token\":0}", "[1,]", "NaN" })
            Test("auth hides parse errors " + invalid.Length, () => Reject(() => new Authentication(Session, Token).Accept(Text(invalid)), WireError.AuthenticationFailed));
        Test("identity forms crypto random and distinct", () =>
        {
            var all = new HashSet<string>();
            foreach (string kind in new[] { "s", "r", "g", "m", "c", "p", "t", "q", "e" })
                for (int i = 0; i < 16; i++) { string id = Ids.New(kind); True(Ids.Is(id, kind)); True(all.Add(id)); }
            True(Ids.IsHex(Ids.NewToken(), 64)); True(!Ids.Is("m_1", "m")); True(!Ids.Is(Session.ToUpperInvariant(), "s"));
        });
        Test("exact scope equality no numeric fallback", () => { True(MapScope().Same(MapScope())); True(!MapScope().Same(new Scope(Session, Ids.New("r")))); });
        Test("map scope requires game", () => Reject(() => new Scope(Session, Root, null, Ids.New("m")), WireError.InvalidRequest));
        Test("menu sample omits tick and distinct captures", () => { var a = new SampleStamp(new Scope(Session, Root), 12, null); var b = new SampleStamp(new Scope(Session, Root), 12, null); True(a.Capture != b.Capture); True(a.Json()["tick"] == null); Equal("root_update_postfix", (string)a.Json()["phase"]!); });
        Test("menu cannot claim tick", () => Reject(() => new SampleStamp(new Scope(Session, Root), 12, 0), WireError.InvalidRequest));
        Test("AnyEventNowOrWaiting refuses even otherwise-ready state", () => Equal<WireError?>(WireError.NotReady, Readiness.Check(true, true, false, true, true, true, true, false)));
        Test("dev refusal precedes loading", () => Equal<WireError?>(WireError.ObservationRefused, Readiness.Check(true, false, true, true, false, true, false, true)));
        Test("menu readiness needs root not tick/game/map", () => Equal<WireError?>(null, Readiness.Check(false, true, false, false, false, false, false, false)));
        Test("scene transition refused", () => Equal<WireError?>(WireError.NotReady, Readiness.Check(false, true, true, false, false, false, false, false)));
        Test("required map invalid refused", () => Equal<WireError?>(WireError.NotReady, Readiness.Check(false, true, false, true, true, true, false, false)));

        Test("framing partial reads and big-endian roundtrip", () =>
        {
            byte[] body = Text("{\"雪\":true}"); using var wire = new MemoryStream();
            Framing.WriteAsync(wire, body, WireLimits.RequestBytes, CancellationToken.None).GetAwaiter().GetResult();
            byte[] encoded = wire.ToArray(); Equal((byte)body.Length, encoded[3]);
            using var split = new SplitStream(encoded, 1);
            True(body.SequenceEqual(Framing.ReadAsync(split, WireLimits.RequestBytes, CancellationToken.None).GetAwaiter().GetResult()));
        });
        foreach (uint length in new uint[] { 0, 65537, 0x80000000, 0xffffffff })
            Test("framing reject length before body read " + length, () =>
            {
                using var wire = new SplitStream(new[] { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length }, 4);
                Reject(() => Framing.ReadAsync(wire, WireLimits.RequestBytes, CancellationToken.None).GetAwaiter().GetResult(), WireError.InvalidFrame);
                Equal(1, wire.Reads);
            });
        foreach (byte[] truncated in new[] { Array.Empty<byte>(), new byte[] { 0, 0 }, new byte[] { 0, 0, 0, 4, 1, 2 } })
            Test("framing truncated " + truncated.Length, () => { using var wire = new SplitStream(truncated, 2); Reject(() => Framing.ReadAsync(wire, WireLimits.RequestBytes, CancellationToken.None).GetAwaiter().GetResult(), WireError.InvalidFrame); });
        Test("coalesced frames remain separate", () =>
        {
            using var wire = new MemoryStream();
            foreach (string text in new[] { "{}", "[]" }) Framing.WriteAsync(wire, Text(text), WireLimits.RequestBytes, CancellationToken.None).GetAwaiter().GetResult();
            wire.Position = 0;
            Equal("{}", Encoding.UTF8.GetString(Framing.ReadAsync(wire, WireLimits.RequestBytes, CancellationToken.None).GetAwaiter().GetResult()));
            Equal("[]", Encoding.UTF8.GetString(Framing.ReadAsync(wire, WireLimits.RequestBytes, CancellationToken.None).GetAwaiter().GetResult()));
        });
        Test("maximum ingress length accepted", () =>
        {
            using var wire = new MemoryStream(); var body = new byte[WireLimits.RequestBytes];
            Framing.WriteAsync(wire, body, WireLimits.RequestBytes, CancellationToken.None).GetAwaiter().GetResult(); wire.Position = 0;
            Equal(body.Length, Framing.ReadAsync(wire, WireLimits.RequestBytes, CancellationToken.None).GetAwaiter().GetResult().Length);
        });
        Test("cancelled frame reads no stream bytes", () =>
        {
            using var token = new CancellationTokenSource(); token.Cancel(); using var wire = new SplitStream(new byte[] { 0, 0, 0, 1, 0 }, 1);
            try { Framing.ReadAsync(wire, WireLimits.RequestBytes, token.Token).GetAwaiter().GetResult(); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { Equal(0, wire.Reads); }
        });
        Test("oversize output rejected before header", () => { using var wire = new MemoryStream(); Reject(() => Framing.WriteAsync(wire, new byte[65537], WireLimits.RequestBytes, CancellationToken.None).GetAwaiter().GetResult(), WireError.InvalidFrame); Equal(0L, wire.Length); });
        Test("scan budgets finite across updates", () =>
        {
            var scan = new ScanBudget(); int count = 0;
            for (int i = 0; i < WireLimits.ScanCandidates / WireLimits.CandidatesPerUpdate; i++)
            {
                scan.BeginUpdate(); for (int j = 0; j < WireLimits.CandidatesPerUpdate; j++) { True(scan.TryExamine()); count++; }
                True(!scan.TryExamine());
            }
            scan.BeginUpdate(); True(!scan.TryExamine()); Equal(WireLimits.ScanCandidates, count);
        });
        Test("page serializes only public eligible vector no progress totals", () =>
        {
            var page = Responses.Page(new SampleStamp(MapScope(), 12, 1), Ids.New("q"), new JArray(), null);
            Equal("basis,complete,items,sample,snapshot", string.Join(",", page.Properties().Select(p => p.Name).OrderBy(n => n)));
            ResponseContracts.Read(WireJson.Encode(Responses.Success("p", page)), ReadMethod.Pawns, "p");
        });
        Test("page max enforced", () => Reject(() => Responses.Page(new SampleStamp(MapScope(), 12, 1), Ids.New("q"), new JArray(Enumerable.Repeat(0, 65)), null), WireError.InvalidRequest));
        foreach (WireError code in Enum.GetValues<WireError>())
            Test("fixed safe error " + code, () =>
            {
                JObject error = Responses.Error("not-valid-credential/secret", code);
                True(error["id"] == null); Equal(1, ((JObject)error["error"]!).Count);
                True(!Encoding.UTF8.GetString(WireJson.Encode(error)).Contains("secret", StringComparison.Ordinal));
            });
        Test("duplicate capability names rejected", () => Reject(() => Responses.Capabilities(new[] { new Capability("x", CapabilityState.Unsupported, "none"), new Capability("x", CapabilityState.Available, "x-v1") }), WireError.InvalidRequest));
        Test("JSON global serializer defaults not used by wire writer", () =>
        {
            var previous = JsonConvert.DefaultSettings;
            try { JsonConvert.DefaultSettings = () => throw new Exception("Global serializer invoked"); WireJson.Encode(Responses.Error("x", WireError.Unavailable)); }
            finally { JsonConvert.DefaultSettings = previous; }
        });
        Test("ordinary response byte ceiling enforced", () =>
        {
            var data = new JObject { ["text"] = new string('a', WireLimits.ResponseBytes) };
            Reject(() => WireJson.Encode(Responses.Success("x", data)), WireError.ResponseTooLarge);
        });
        Test("max screenshot response fits separate bounded profile", () =>
        {
            JObject data = Responses.Capture(CompletedCapture(), 1920, 1080, 1, new byte[WireLimits.CapturePngBytes]);
            byte[] bytes = WireJson.Encode(Responses.Success("i", data), true);
            True(bytes.Length <= WireLimits.CaptureResponseBytes);
            ResponseContracts.Read(bytes, ReadMethod.Screenshot, "i");
            Reject(() => WireJson.Encode(Responses.Success("i", data)), WireError.ResponseTooLarge);
        });
        Test("capture cannot claim oversized resolution", () => Reject(() => Responses.Capture(CompletedCapture(), 1921, 1080, 1, new byte[8]), WireError.ResponseTooLarge));

        JObject responses = Fixture("responses.json");
        foreach (JToken entry in (JArray)responses["accepted"]!)
            Test("response fixture " + (entry["id"]?.ToString() ?? "preauth"), () =>
            {
                string id = (string?)entry["id"] ?? "a";
                ResponseContracts.Read(Bytes(entry), MethodFor(id), id);
            });
        foreach (JToken entry in (JArray)responses["rejected"]!)
            Test("response fixture rejects " + entry.ToString(Formatting.None), () => Reject(() => ResponseContracts.Read(Bytes(entry), entry["data"]?["capabilities"] != null ? ReadMethod.Capabilities : ReadMethod.Events, "x"), WireError.InvalidRequest));
        Test("no response coercion or unrecognized hidden fields", () =>
        {
            JObject data = (JObject)responses["accepted"]![4]!.DeepClone(); data["data"]!["pawn"]!["health"]![0]!["hiddenSeverity"] = 0.8;
            Reject(() => ResponseContracts.Read(Bytes(data), ReadMethod.Pawn, "d"), WireError.InvalidRequest);
        });
        Test("response ID must match pending request", () => Reject(() => ResponseContracts.Read(Bytes(responses["accepted"]![0]!), ReadMethod.Auth, "different"), WireError.InvalidRequest));
        Test("capture timing not conflated with JSON sample", () =>
        {
            JObject data = (JObject)responses["accepted"]![9]!.DeepClone(); data["data"]!["phase"] = "root_update_postfix";
            Reject(() => ResponseContracts.Read(Bytes(data), ReadMethod.Screenshot, "i"), WireError.InvalidRequest);
        });
        Test("large response profile cannot widen request/auth ingress", () => Reject(() => Request.Read(new byte[WireLimits.RequestBytes + 1]), WireError.InvalidJson));
        Test("response encoder validates method-specific DTO before output", () =>
        {
            var page = Responses.Page(new SampleStamp(MapScope(), 12, 1), Ids.New("q"), new JArray(new JObject { ["thing"] = Ids.New("t"), ["label"] = "Steel", ["x"] = 1, ["z"] = 1 }), null);
            WireJson.EncodeResponse("t", ReadMethod.Things, page);
            Reject(() => WireJson.EncodeResponse("p", ReadMethod.Pawns, page), WireError.InvalidRequest);
        });
        Test("no partial-page hidden scan counters", () =>
        {
            JObject response = (JObject)responses["accepted"]![5]!.DeepClone(); response["data"]!["scanned"] = 3;
            Reject(() => ResponseContracts.Read(Bytes(response), ReadMethod.Pawns, "p"), WireError.InvalidRequest);
        });
        Test("page cursor required exactly when incomplete", () =>
        {
            JObject response = (JObject)responses["accepted"]![5]!.DeepClone(); ((JObject)response["data"]!).Remove("next");
            Reject(() => ResponseContracts.Read(Bytes(response), ReadMethod.Pawns, "p"), WireError.InvalidRequest);
        });
        Test("PNG base64 padding must match declared decoded bytes", () =>
        {
            JObject response = (JObject)responses["accepted"]![9]!.DeepClone(); response["data"]!["bytes"] = 67;
            Reject(() => ResponseContracts.Read(Bytes(response), ReadMethod.Screenshot, "i"), WireError.InvalidRequest);
        });
        Test("game-only tick cannot appear at menu", () =>
        {
            JObject response = (JObject)responses["accepted"]![3]!.DeepClone(); response["data"]!["sample"]!["tick"] = 1;
            Reject(() => ResponseContracts.Read(Bytes(response), ReadMethod.Summary, "s"), WireError.InvalidRequest);
        });
        Test("unsupported fields absent not synthesized as empty", () =>
        {
            JObject response = (JObject)responses["accepted"]![4]!.DeepClone(); ((JObject)response["data"]!["pawn"]!).Remove("health");
            JObject decoded = ResponseContracts.Read(Bytes(response), ReadMethod.Pawn, "d");
            True(decoded["data"]!["pawn"]!["health"] == null);
        });
        Console.WriteLine($"Wire contracts: {passed} passed; {failed} failed. No listener/game/observation proof.");
        return failed;
    }
    private static CompletedRenderStamp CompletedCapture()
    {
        var scope = new Scope(Session, Root);
        return CompletedRenderStamp.Complete(new CaptureRequestStamp(scope, 1, 11),
            new RenderEvidence(scope, 1, 12, true, true), scope, 1, null);
    }
    private static ReadMethod MethodFor(string id) => id switch { "a" => ReadMethod.Auth, "h" => ReadMethod.Health, "c" => ReadMethod.Capabilities, "s" => ReadMethod.Summary, "d" => ReadMethod.Pawn, "p" => ReadMethod.Pawns, "t" => ReadMethod.Things, "r" => ReadMethod.Resources, "u" => ReadMethod.Ui, "i" => ReadMethod.Screenshot, "e" => ReadMethod.Events, _ => ReadMethod.Health };
    private static void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS wire " + name); } catch (Exception error) { failed++; Console.Error.WriteLine("FAIL wire " + name + ": " + error); } }
    private static void Reject(Action action, WireError code) { try { action(); } catch (WireException error) { Equal(code, error.Code); Equal("Bridge request rejected.", error.Message); True(error.InnerException == null); return; } throw new Exception("Expected " + code); }
    private static void True(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    private sealed class SplitStream : MemoryStream
    {
        private readonly int chunk;
        internal int Reads { get; private set; }
        internal SplitStream(byte[] bytes, int chunk) : base(bytes) { this.chunk = chunk; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { Reads++; return base.ReadAsync(buffer, offset, Math.Min(chunk, count), token); }
    }
}
