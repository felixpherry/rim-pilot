using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimPilot.Cli;
using RimPilot.Mod.Bridge;
using RimPilot.Mod.Observation;
using RimPilot.Protocol;

[SupportedOSPlatform("linux")]
internal static class ObservationClientTests
{
    private static int passed, failed;
    internal static int Run()
    {
        foreach (string method in new[] { "summary", "pawns.list", "pawn.get", "things.list", "resources", "ui", "events.read", "screenshot" })
            Test("typed client / real transport + dispatcher producer " + method, async () =>
            {
                using var f = new Fixture();
                string path = Path.Combine(f.Root, "capture.png");
                var args = f.Arguments(method).ToList();
                if (method == "screenshot") args.AddRange(new[] { "--output", path });
                var result = await f.Command(args.ToArray());
                Equal(0, result.Exit); Equal("", result.Error);
                JObject wrapper = JObject.Parse(result.Output);
                Equal(f.Server.Session, (string)wrapper["session"]!);
                True((bool)wrapper["response"]!["ok"]!);
                Equal(method == "pawn.get" ? ReadMethod.Pawn : ObservationCommand.Parse(args.ToArray()).Method, f.Last!.Method);
                True(f.Scope.Same(f.Last.Scope!));
                if (method == "screenshot")
                {
                    True(File.ReadAllBytes(path).SequenceEqual(f.Png));
                    True(wrapper["response"]!["data"]!["chunks"] == null);
                    Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                }
                Equal(1, f.Starts); True(!result.Output.Contains(f.Secret, StringComparison.Ordinal));
            });
        Test("health scope bootstrap uses same authenticated connection", async () =>
        {
            using var f = new Fixture();
            // Health root/game scope deliberately has no map; summary accepts that exact scope.
            f.Expected = f.Dispatch.Generations.Scope!;
            var result = await f.Command("summary", "--session", f.Server.Session);
            Equal(0, result.Exit); True(f.Expected.Same(f.Last!.Scope!)); Equal(1, f.Starts);
        });
        foreach (string method in new[] { "pawns.list", "things.list", "events.read" })
            Test("reusable opaque page/event cursor and limit " + method, async () =>
            {
                using var f = new Fixture(); string cursor = Ids.New(method == "events.read" ? "e" : "q");
                string[] args = f.Arguments(method).Concat(new[] { "--limit", "1", "--cursor", cursor }).ToArray();
                for (int i = 0; i < 2; i++)
                {
                    var result = await f.Command(args); Equal(0, result.Exit);
                    Equal(cursor, f.Last!.Cursor); Equal(1, f.Last.Limit);
                }
            });
        foreach (WireError code in new[] { WireError.CursorExpired, WireError.EventGap, WireError.StaleScope, WireError.ObservationRefused })
            Test("explicit failure no retarget/reconnect " + code, async () =>
            {
                using var f = new Fixture(); f.Error = code;
                var result = await f.Command(f.Arguments("events.read"));
                Equal(69, result.Exit); True(!(bool)JObject.Parse(result.Output)["response"]!["ok"]!);
                Equal(1, f.Starts);
            });
        foreach (string failure in new[] { "session", "root", "game", "map", "pawn", "limit", "cursor", "image", "bytes", "dimensions" })
            Test("malformed response withheld " + failure, async () =>
            {
                using var f = new Fixture();
                string method = failure is "image" or "bytes" or "dimensions" ? "screenshot"
                    : failure == "pawn" ? "pawn.get" : "pawns.list";
                f.Mutate = data => {
                    if (failure is "session" or "root" or "game" or "map")
                        data["sample"]!["scope"]![failure] = Ids.New(failure.Substring(0, 1));
                    if (failure == "pawn") data["pawn"]!["pawn"] = Ids.New("p");
                    if (failure == "limit") ((JArray)data["items"]!).Add(data["items"]![0]!.DeepClone());
                    if (failure == "cursor") { data["complete"] = false; data["next"] = Ids.New("e"); }
                    if (failure == "image") { byte[] image = (byte[])f.Png.Clone(); image[image.Length - 1] ^= 1; data["chunks"] = Chunks(image); }
                    if (failure == "bytes") data["bytes"] = f.Png.Length - 1;
                    if (failure == "dimensions") data["width"] = 3;
                };
                string path = Path.Combine(f.Root, "never.png");
                string[] args = f.Arguments(method).Concat(method == "screenshot" ? new[] { "--output", path }
                    : method == "pawns.list" ? new[] { "--limit", "1" } : Array.Empty<string>()).ToArray();
                var result = await f.Command(args); Equal(69, result.Exit);
                if (failure is "bytes" or "cursor")
                    True(!(bool)JObject.Parse(result.Output)["response"]!["ok"]!); // Server schema rejects before transfer.
                else Equal("", result.Output);
                True(!File.Exists(path)); Equal(0, Directory.GetFiles(f.Root, ".rim-pilot-*.tmp").Length); Equal(1, f.Starts);
            });
        Test("screenshot profile accepts response above ordinary JSON ceiling", async () =>
        {
            using var f = new Fixture();
            f.Png = BoundedCapturePng.Encode(new CaptureSize(400, 300), new byte[400 * 300 * 3], () => true);
            f.Width = 400; f.Height = 300;
            var result = await f.Command(f.Arguments("screenshot").Concat(new[] { "--output", Path.Combine(f.Root, "large.png") }).ToArray());
            True(result.Exit == 0, $"large screenshot failed: exit={result.Exit}; stderr={result.Error}; response={result.Output}");
        });
        Test("local output collision preserves file and cleans stage", async () =>
        {
            using var f = new Fixture(); string path = Path.Combine(f.Root, "keep.png"); File.WriteAllText(path, "keep");
            var result = await f.Command(f.Arguments("screenshot").Concat(new[] { "--output", path }).ToArray());
            Equal(69, result.Exit); Equal("", result.Output); Equal("keep", File.ReadAllText(path));
            Equal(0, Directory.GetFiles(f.Root, ".rim-pilot-*.tmp").Length);
        });
        Test("local symlink collision never writes target", async () =>
        {
            using var f = new Fixture(); string target = Path.Combine(f.Root, "keep"), link = Path.Combine(f.Root, "link");
            File.WriteAllText(target, "keep"); File.CreateSymbolicLink(link, target);
            try { PngOutput.Write(link, f.Png); throw new Exception("overwrote symlink"); } catch (IOException) { }
            Equal("keep", File.ReadAllText(target)); Equal(target, new FileInfo(link).LinkTarget);
            Equal(0, Directory.GetFiles(f.Root, ".rim-pilot-*.tmp").Length); await Task.CompletedTask;
        });
        foreach (string[] args in new[] {
            new[] { "click" }, new[] { "summary", "--output", "secret" }, new[] { "ui", "--cursor", Ids.New("q") },
            new[] { "screenshot" }, new[] { "pawns.list" }, new[] { "summary", "--game", Ids.New("g") },
            new[] { "summary", "--root", "bad" }, new[] { "events.read", "--cursor", Ids.New("q") },
            new[] { "events.read", "--limit", "65" }, new[] { "events.read", "--limit", "0" },
            new[] { "events.read", "--limit", "1", "--limit", "2" } })
            Test("closed command arguments " + string.Join(' ', args.Take(2)), async () => {
                try { ObservationCommand.Parse(args); throw new Exception("accepted invalid command"); } catch (ArgumentException) { }
                await Task.CompletedTask;
            });
        Test("complete PNG validation and malformed content", async () =>
        {
            byte[] png = BoundedCapturePng.Encode(new CaptureSize(2, 2), new byte[12], () => true);
            PngOutput.Validate(png, 2, 2);
            // Every truncated byte boundary, trailing bytes, wrong dimensions, every CRC-covered byte.
            for (int n = 0; n < png.Length; n++) Invalid(() => PngOutput.Validate(png.Take(n).ToArray(), 2, 2));
            Invalid(() => PngOutput.Validate(png.Concat(new byte[] { 0 }).ToArray(), 2, 2));
            Invalid(() => PngOutput.Validate(png, 1, 2));
            for (int n = 0; n < png.Length; n++)
            { byte[] changed = (byte[])png.Clone(); changed[n] ^= 1; Invalid(() => PngOutput.Validate(changed, 2, 2)); }
            // Recompute chunk CRC: corrupt zlib header, block length, filter, Adler, final bit,
            // too-short/too-long decompressed scanlines must still fail beyond PNG chunk checks.
            foreach (int offset in new[] { 41, 42, 43, 44, 46, 48, png.Length - 17 })
            {
                byte[] changed = (byte[])png.Clone(); changed[offset] ^= offset == 48 ? (byte)7 : (byte)1;
                FixIdatCrc(changed); Invalid(() => PngOutput.Validate(changed, 2, 2));
            }
            await Task.CompletedTask;
        });
        Test("compressed RGB/RGBA and bounded inflation/filter/EOF checks", async () =>
        {
            foreach (int channels in new[] { 3, 4 })
            {
                var rows = new byte[2 * (2 * channels + 1)]; rows[0] = 4;
                PngOutput.Validate(Image(rows, channels), 2, 2);
                Invalid(() => PngOutput.Validate(Image(rows.Concat(new byte[] { 0 }).ToArray(), channels), 2, 2));
                Invalid(() => PngOutput.Validate(Image(rows.Take(rows.Length - 1).ToArray(), channels), 2, 2));
                rows[0] = 5; Invalid(() => PngOutput.Validate(Image(rows, channels), 2, 2));
                rows[0] = 0;
                Invalid(() => PngOutput.Validate(Image(rows, channels, trailing: true), 2, 2));
            }
            await Task.CompletedTask;
        });
        Test("base64 canonical padding and declared length", async () =>
        {
            using var f = new Fixture();
            JObject data = new() { ["bytes"] = f.Png.Length, ["width"] = 2, ["height"] = 2, ["chunks"] = Chunks(f.Png) };
            True(PngOutput.Decode(data).SequenceEqual(f.Png));
            data["bytes"] = f.Png.Length + 1; Invalid(() => PngOutput.Decode(data));
            data["bytes"] = f.Png.Length;
            string text = Convert.ToBase64String(f.Png); True(text.EndsWith('='));
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            int at = text.Length - (text.EndsWith("==", StringComparison.Ordinal) ? 3 : 2);
            int index = alphabet.IndexOf(text[at]);
            data["chunks"] = new JArray(text.Substring(0, at) + alphabet[index + 1] + text.Substring(at + 1));
            Invalid(() => PngOutput.Decode(data)); await Task.CompletedTask;
        });
        Test("local output missing directory leaves no partial image", async () =>
        {
            using var f = new Fixture();
            string path = Path.Combine(f.Root, "absent", "image.png");
            var result = await f.Command(f.Arguments("screenshot").Concat(new[] { "--output", path }).ToArray());
            Equal(69, result.Exit); Equal("", result.Output); True(!Directory.Exists(Path.GetDirectoryName(path)));
        });
        Console.WriteLine($"Observation CLI: {passed} passed; {failed} failed. Host transport/producer/image only; Mono/game NOT RUN.");
        return failed;
    }

    private sealed class Fixture : IDisposable, IReadProducer<object>
    {
        internal readonly string Root = Directory.CreateTempSubdirectory("rim-pilot-observe-client-").FullName;
        internal readonly LoopbackTransport Server;
        internal readonly ReadDispatcher<object> Dispatch;
        private readonly object root = new(), game = new();
        private readonly List<object> maps = new() { new() };
        internal Scope Scope => Dispatch.Generations.MapScope(maps[0])!;
        internal Scope? Expected;
        internal string Pawn = Ids.New("p"), Secret;
        internal byte[] Png = BoundedCapturePng.Encode(new CaptureSize(2, 2), new byte[12], () => true);
        internal int Width = 2, Height = 2, Starts;
        internal Request? Last;
        internal Action<JObject>? Mutate;
        internal WireError? Error;
        public bool SnapshotBuild => false;
        internal Fixture()
        {
            File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Server = LoopbackTransport.Start(Root);
            using (var registry = LinuxPrivateDirectory.Open(Root)) Secret = registry.ReadRecord(Server.Session + ".json", new DiscoveryBudget()).Token;
            Dispatch = new ReadDispatcher<object>(Server.Session, Server.Pending, new TransitionFence(Server.Pending), () => 0,
                (ReadMethod.Summary, this), (ReadMethod.Pawns, this), (ReadMethod.Pawn, this), (ReadMethod.Things, this),
                (ReadMethod.Resources, this), (ReadMethod.Ui, this), (ReadMethod.Events, this), (ReadMethod.Screenshot, this));
            Pump();
        }
        private void Pump() => Dispatch.Pump(() => new ReadFrame<object>(root, game, maps, true, DeveloperSafety.Safe,
            new FrameGeometry(1, 1280, 720, 1), _ => true));
        internal string[] Arguments(string method)
        {
            var result = new List<string> { method, "--session", Server.Session, "--root", Scope.Root,
                "--game", Scope.Game!, "--map", Scope.Map! };
            if (method == "pawn.get") result.AddRange(new[] { "--pawn", Pawn });
            return result.ToArray();
        }
        internal async Task<(int Exit, string Output, string Error)> Command(params string[] args)
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Task<int> task = CliCommands.RunAsync(args, Root, output, error);
            var clock = Stopwatch.StartNew();
            while (!task.IsCompleted) { True(clock.ElapsedMilliseconds < 6000); Pump(); Thread.Sleep(1); }
            return (await task, output.ToString(), error.ToString());
        }
        public IReadContinuation<object> Start(Request request) { Last = request; Starts++; return new Continuation(this, request); }
        public void Dispose() { Server.Dispose(); Directory.Delete(Root, true); }
        private sealed class Continuation(Fixture f, Request request) : IReadContinuation<object>
        {
            public StepResult Step(ProducerWork<object> work)
            {
                if (f.Error.HasValue) work.Fail(f.Error.Value);
                work.TryCandidate(); return StepResult.Complete;
            }
            public bool Eligible(ProducerWork<object> work) => true;
            public JObject Result()
            {
                Scope scope = f.Expected ?? request.Scope!;
                var sample = new SampleStamp(scope, 1, 0).Json();
                JObject pawn = new() { ["pawn"] = f.Pawn, ["label"] = "Colony 雪" };
                JObject data = request.Method switch {
                    ReadMethod.Summary => new JObject { ["sample"] = sample, ["scene"] = "playing", ["build"] = "synthetic" },
                    ReadMethod.Pawn => new JObject { ["sample"] = sample, ["pawn"] = pawn },
                    ReadMethod.Pawns or ReadMethod.Things => new JObject { ["sample"] = sample, ["snapshot"] = Ids.New("q"),
                        ["basis"] = "eligible_observed_snapshot", ["items"] = new JArray(request.Method == ReadMethod.Pawns ? pawn
                            : new JObject { ["thing"] = Ids.New("t"), ["label"] = "Steel", ["x"] = 0, ["z"] = 0 }), ["complete"] = true },
                    ReadMethod.Resources => new JObject { ["sample"] = sample, ["basis"] = "eligible_observed_direct_spawned_stack_units", ["items"] = new JArray() },
                    ReadMethod.Ui => new JObject { ["sample"] = sample, ["windows"] = new JArray() },
                    ReadMethod.Events => new JObject { ["scope"] = scope.Json(), ["items"] = new JArray(), ["next"] = Ids.New("e") },
                    ReadMethod.Screenshot => new JObject { ["scope"] = scope.Json(), ["capture"] = Ids.New("c"), ["frame"] = 2,
                        ["phase"] = "completed_render", ["width"] = f.Width, ["height"] = f.Height, ["scale"] = 1,
                        ["encoding"] = "png_base64_chunks", ["bytes"] = f.Png.Length, ["chunks"] = Chunks(f.Png) },
                    _ => throw new Exception() };
                f.Mutate?.Invoke(data); return data;
            }
            public void Discard() { }
        }
    }
    private static JArray Chunks(byte[] bytes)
    {
        string text = Convert.ToBase64String(bytes); var chunks = new JArray();
        for (int p = 0; p < text.Length; p += WireLimits.CaptureChunkChars)
            chunks.Add(text.Substring(p, Math.Min(WireLimits.CaptureChunkChars, text.Length - p)));
        return chunks;
    }
    private static byte[] Image(byte[] rows, int channels, bool trailing = false)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Chunk("IHDR", new byte[] { 0, 0, 0, 2, 0, 0, 0, 2, 8, (byte)(channels == 3 ? 2 : 6), 0, 0, 0 });
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) zip.Write(rows);
        byte[] bytes = compressed.ToArray();
        if (trailing) bytes = bytes.Take(bytes.Length - 4).Concat(new byte[] { 0 }).Concat(bytes.TakeLast(4)).ToArray();
        Chunk("IDAT", bytes); Chunk("IEND", Array.Empty<byte>()); return output.ToArray();
        void Chunk(string type, byte[] payload)
        {
            U32((uint)payload.Length); uint crc = uint.MaxValue;
            foreach (byte value in System.Text.Encoding.ASCII.GetBytes(type).Concat(payload))
            {
                output.WriteByte(value); crc ^= value;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
            }
            U32(crc ^ uint.MaxValue);
        }
        void U32(uint value) { for (int i = 24; i >= 0; i -= 8) output.WriteByte((byte)(value >> i)); }
    }
    private static void FixIdatCrc(byte[] png)
    {
        int length = png.Length - 57; // one IDAT, followed by IEND
        uint crc = uint.MaxValue;
        for (int i = 37; i < 41 + length; i++)
        { crc ^= png[i]; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1; }
        crc ^= uint.MaxValue;
        for (int i = 0; i < 4; i++) png[41 + length + i] = (byte)(crc >> (24 - i * 8));
    }
    private static void Invalid(Action action)
    {
        try { action(); throw new Exception("accepted malformed PNG"); } catch (WireException) { }
    }
    private static void Test(string name, Func<Task> action)
    {
        try { action().WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult(); passed++; }
        catch (Exception error) { failed++; Console.Error.WriteLine("FAIL observation CLI " + name + ": " + error); }
    }
    private static void True(bool condition, string message = "assertion failed") { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
}
