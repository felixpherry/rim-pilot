using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using RimPilot.Protocol;

// The test-only partial hook is compiled out of both production targets.
namespace RimPilot.Protocol
{
    internal sealed partial class LinuxPrivateDirectory
    {
        internal Action<string>? TestHook;
        partial void Observe(string point) { TestHook?.Invoke(point); }
        internal static LinuxPrivateDirectory TestOpen(string path, uint? uid = null)
        {
            RequirePlatform();
            return OpenCandidate(new RuntimeDirectory(path, path + "/rim-pilot/v1", false), uid ?? EffectiveUid);
        }
    }
}

[SupportedOSPlatform("linux")]
internal static class NativeFilesystemTests
{
    private static int passed, failed;
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static DiscoveryDescriptor Descriptor(string hex = "a", int port = 23456) =>
        new DiscoveryDescriptor("s_" + new string(hex[0], 32), port, new string('c', 64));

    internal static int Run()
    {
        Test("native publish/read/remove and bounded enumeration", () =>
        {
            using var fixture = new Scratch();
            using var registry = fixture.Open();
            using var owned = registry.Publish(Descriptor());
            Equal(PrivateDirectory, File.GetUnixFileMode(fixture.Registry));
            Equal(PrivateFile, File.GetUnixFileMode(fixture.Record));
            Equal(Descriptor().Token, registry.ReadRecord(Descriptor().RecordName, new DiscoveryBudget()).Token);
            True(registry.EnumerateNames(new DiscoveryBudget()).Contains(Descriptor().RecordName));
            True(!owned.ToString().Contains(Descriptor().Token, StringComparison.Ordinal));
            True(registry.RemoveOwned(owned));
            True(!File.Exists(fixture.Record));
            Reject(() => registry.RemoveOwned(owned));
        });
        Test("production selector retains exact Unicode primary directory", () =>
        {
            using var fixture = new Scratch();
            string primary = fixture.Base + "/雪 😀";
            Directory.CreateDirectory(primary, PrivateDirectory);
            using var registry = LinuxPrivateDirectory.Open(primary);
            using var owned = registry.Publish(Descriptor());
            True(File.Exists(primary + "/rim-pilot/v1/" + Descriptor().RecordName));
            True(registry.RemoveOwned(owned));
        });
        Test("receipt disposal alone does not delete a published record", () =>
        {
            using var fixture = new Scratch();
            using var registry = fixture.Open();
            registry.Publish(Descriptor()).Dispose();
            True(File.Exists(fixture.Record));
            True(registry.EnumerateNames(new DiscoveryBudget()).Length == 1);
        });
        foreach (string kind in new[] { "file", "link", "directory" })
            Test("no-overwrite publication preserves existing " + kind, () =>
            {
                using var fixture = new Scratch();
                using var registry = fixture.Open();
                string sentinel = Path.Combine(fixture.Root, "sentinel");
                File.WriteAllText(sentinel, "untouched");
                if (kind == "file") fixture.Write(Encoding.UTF8.GetBytes("existing"));
                if (kind == "link") File.CreateSymbolicLink(fixture.Record, sentinel);
                if (kind == "directory") Directory.CreateDirectory(fixture.Record);
                Reject(() => registry.Publish(Descriptor()));
                Equal("untouched", File.ReadAllText(sentinel));
                if (kind == "file") Equal("existing", File.ReadAllText(fixture.Record));
                if (kind == "link") True(new FileInfo(fixture.Record).LinkTarget == sentinel);
                if (kind == "directory") True(Directory.Exists(fixture.Record));
                True(Directory.GetFileSystemEntries(fixture.Registry).Length == 1);
            });
        Test("parallel publishers admit exactly one and preserve winner bytes", () =>
        {
            using var fixture = new Scratch();
            using var left = fixture.Open();
            using var right = fixture.Open();
            var first = Descriptor("a", 12345);
            var second = Descriptor("a", 54321);
            LinuxPrivateDirectory.OwnedRecord? winner = null;
            LinuxPrivateDirectory? owner = null;
            int successes = 0;
            System.Threading.Tasks.Parallel.Invoke(
                () => Attempt(left, first), () => Attempt(right, second));
            True(successes == 1 && winner != null && owner != null);
            using (winner!) True(owner!.RemoveOwned(winner!));
            void Attempt(LinuxPrivateDirectory candidate, DiscoveryDescriptor descriptor)
            {
                try
                {
                    var record = candidate.Publish(descriptor);
                    System.Threading.Interlocked.Increment(ref successes);
                    winner = record; owner = candidate;
                }
                catch (DiscoveryException) { }
            }
        });
        foreach (int mode in new[] { 0, 256, 420, 438, 385, 2432 })
            Test("record refuses non-0600 mode " + mode, () =>
            {
                using var fixture = new Scratch();
                using var registry = fixture.Open();
                fixture.Write(Descriptor().EncodePrivate());
                File.SetUnixFileMode(fixture.Record, (UnixFileMode)mode);
                Reject(() => registry.ReadRecord(Descriptor().RecordName, new DiscoveryBudget()));
            });
        foreach (int mode in new[] { 0, 493, 511, 1472, 2496 })
            Test("private base refuses unsafe mode " + mode, () =>
            {
                using var fixture = new Scratch();
                File.SetUnixFileMode(fixture.Base, (UnixFileMode)mode);
                Reject(() => fixture.Open());
                Equal((UnixFileMode)mode, File.GetUnixFileMode(fixture.Base)); // Never chmod-repair.
            });
        Test("actual native ownership metadata rejects different expected UID", () =>
        {
            using var fixture = new Scratch();
            Reject(() => LinuxPrivateDirectory.TestOpen(fixture.Base, LinuxPrivateDirectory.EffectiveUid + 1));
        });
        Test("writable nonsticky ancestor refuses traversal", () =>
        {
            using var fixture = new Scratch();
            File.SetUnixFileMode(fixture.Root, (UnixFileMode)511);
            Reject(() => fixture.Open());
        });
        foreach (string level in new[] { "ancestor", "base", "rim-pilot", "v1" })
            Test("no-follow directory traversal refuses " + level + " symlink", () =>
            {
                using var fixture = new Scratch();
                using (fixture.Open()) { }
                string target = level == "ancestor" ? fixture.Root : level == "base" ? fixture.Base
                    : level == "rim-pilot" ? Path.Combine(fixture.Base, "rim-pilot") : fixture.Registry;
                Directory.Move(target, target + "-real");
                Directory.CreateSymbolicLink(target, target + "-real");
                try { Reject(() => fixture.Open()); }
                finally { Directory.Delete(target); Directory.Move(target + "-real", target); }
            });
        foreach (string kind in new[] { "symlink", "dangling", "fifo", "directory", "hardlink", "empty", "oversize" })
            Test("record rejects " + kind + " without following/blocking", () =>
            {
                using var fixture = new Scratch();
                using var registry = fixture.Open();
                string other = Path.Combine(fixture.Root, "other");
                File.WriteAllText(other, "sentinel");
                if (kind == "symlink") File.CreateSymbolicLink(fixture.Record, other);
                if (kind == "dangling") File.CreateSymbolicLink(fixture.Record, other + "-missing");
                if (kind == "fifo") True(mkfifo(fixture.Record, 384) == 0);
                if (kind == "directory") Directory.CreateDirectory(fixture.Record);
                if (kind == "hardlink") { fixture.Write(Descriptor().EncodePrivate()); True(link(fixture.Record, other + "-hardlink") == 0); }
                if (kind == "empty") fixture.Write(Array.Empty<byte>());
                if (kind == "oversize") fixture.Write(new byte[DiscoveryLimits.DescriptorBytes + 1]);
                var clock = Stopwatch.StartNew();
                Reject(() => registry.ReadRecord(Descriptor().RecordName, new DiscoveryBudget()));
                True(clock.ElapsedMilliseconds < 2000);
                Equal("sentinel", File.ReadAllText(other));
            });
        Test("bounded native read accepts exactly 1024 and rejects 1025 bytes", () =>
        {
            using var fixture = new Scratch();
            using var registry = fixture.Open();
            byte[] original = Descriptor().EncodePrivate();
            byte[] padded = Enumerable.Repeat((byte)' ', DiscoveryLimits.DescriptorBytes).ToArray();
            original.CopyTo(padded, 0);
            fixture.Write(padded);
            Equal(Descriptor().Session, registry.ReadRecord(Descriptor().RecordName, new DiscoveryBudget()).Session);
            fixture.Write(padded.Concat(new byte[] { (byte)' ' }).ToArray());
            Reject(() => registry.ReadRecord(Descriptor().RecordName, new DiscoveryBudget()));
        });
        foreach (string name in new[] { "../s.json", "/tmp/record", "s_" + new string('a', 32) + ".json/../x", "bad\0name", ".", "" })
            Test("reject record path/name input", () =>
            {
                using var fixture = new Scratch();
                using var registry = fixture.Open();
                Reject(() => registry.ReadRecord(name, new DiscoveryBudget()));
            });
        foreach (string mutation in new[] { "grow", "shrink", "mode", "replace", "hardlink" })
            Test("read rejects mutation after fd open: " + mutation, () =>
            {
                using var fixture = new Scratch();
                using var registry = fixture.Open();
                fixture.Write(Descriptor().EncodePrivate());
                registry.TestHook = point =>
                {
                    if (point != "before-record-read") return;
                    if (mutation == "grow") File.AppendAllText(fixture.Record, new string(' ', 1025));
                    if (mutation == "shrink") File.WriteAllText(fixture.Record, "");
                    if (mutation == "mode") File.SetUnixFileMode(fixture.Record, (UnixFileMode)420);
                    if (mutation == "replace") { File.Move(fixture.Record, fixture.Record + "-original"); fixture.Write(Descriptor().EncodePrivate()); }
                    if (mutation == "hardlink") True(link(fixture.Record, fixture.Record + "-hardlink") == 0);
                };
                Reject(() => registry.ReadRecord(Descriptor().RecordName, new DiscoveryBudget()));
            });
        Test("symlink replacement before open refuses target", () =>
        {
            using var fixture = new Scratch();
            using var registry = fixture.Open();
            fixture.Write(Descriptor().EncodePrivate());
            string sentinel = Path.Combine(fixture.Root, "sentinel");
            File.WriteAllText(sentinel, "untouched");
            registry.TestHook = point =>
            {
                if (point != "before-record-open") return;
                File.Delete(fixture.Record); File.CreateSymbolicLink(fixture.Record, sentinel);
            };
            Reject(() => registry.ReadRecord(Descriptor().RecordName, new DiscoveryBudget()));
            Equal("untouched", File.ReadAllText(sentinel));
        });
        Test("renamed ancestor never retargets an already opened registry", () =>
        {
            using var fixture = new Scratch();
            using var registry = fixture.Open();
            fixture.Write(Descriptor().EncodePrivate());
            registry.TestHook = point =>
            {
                if (point != "before-record-open") return;
                Directory.Move(fixture.Registry, fixture.Registry + "-old");
                Directory.CreateDirectory(fixture.Registry, PrivateDirectory);
                fixture.Write(Descriptor("b").EncodePrivate());
            };
            Reject(() => registry.ReadRecord(Descriptor().RecordName, new DiscoveryBudget()));
            Reject(() => registry.Publish(Descriptor()));
            Equal(Descriptor("b").Token, DiscoveryDescriptor.Read(File.ReadAllBytes(fixture.Record), Descriptor("b").RecordName).Token);
        });
        foreach (string mutation in new[] { "mode", "hardlink" })
            Test("privacy drift before first secret write refuses publication: " + mutation, () =>
            {
                using var fixture = new Scratch();
                using var registry = fixture.Open();
                registry.TestHook = point =>
                {
                    if (point != "before-secret-write") return;
                    string staged = Directory.GetDirectories(fixture.Registry).Single() + "/record";
                    if (mutation == "mode") File.SetUnixFileMode(staged, (UnixFileMode)420);
                    else True(link(staged, fixture.Root + "/hardlink") == 0);
                    Equal(0L, new FileInfo(staged).Length);
                };
                Reject(() => registry.Publish(Descriptor()));
                True(!File.Exists(fixture.Record));
                if (mutation == "hardlink") Equal(0L, new FileInfo(fixture.Root + "/hardlink").Length);
            });
        Test("publication rejects staged name replacement", () =>
        {
            using var fixture = new Scratch();
            using var registry = fixture.Open();
            registry.TestHook = point =>
            {
                if (point != "before-publication") return;
                string staged = Directory.GetDirectories(fixture.Registry).Single() + "/record";
                File.Move(staged, staged + "-original");
                File.WriteAllText(staged, "replacement");
                File.SetUnixFileMode(staged, PrivateFile);
            };
            Reject(() => registry.Publish(Descriptor()));
            True(!File.Exists(fixture.Record));
            Equal("replacement", File.ReadAllText(Directory.GetDirectories(fixture.Registry).Single() + "/record"));
        });
        foreach (string mutation in new[] { "rewrite", "mode", "hardlink", "replace", "missing", "symlink" })
            Test("owned removal preserves changed record: " + mutation, () =>
            {
                using var fixture = new Scratch();
                using var registry = fixture.Open();
                using var owned = registry.Publish(Descriptor());
                if (mutation == "rewrite") File.WriteAllText(fixture.Record, "changed");
                if (mutation == "mode") File.SetUnixFileMode(fixture.Record, (UnixFileMode)420);
                if (mutation == "hardlink") True(link(fixture.Record, fixture.Record + "-hardlink") == 0);
                if (mutation == "replace") { File.Move(fixture.Record, fixture.Record + "-original"); fixture.Write(Descriptor().EncodePrivate()); }
                if (mutation == "missing") File.Delete(fixture.Record);
                if (mutation == "symlink") { File.Move(fixture.Record, fixture.Record + "-original"); File.CreateSymbolicLink(fixture.Record, fixture.Record + "-original"); }
                True(!registry.RemoveOwned(owned));
                if (mutation != "missing") True(File.Exists(fixture.Record));
            });
        Test("raced replacement before quarantine is restored, not deleted", () =>
        {
            using var fixture = new Scratch();
            using var registry = fixture.Open();
            using var owned = registry.Publish(Descriptor());
            registry.TestHook = point =>
            {
                if (point != "before-removal-move") return;
                File.Move(fixture.Record, fixture.Record + "-original");
                fixture.Write(Encoding.UTF8.GetBytes("replacement"));
            };
            True(!registry.RemoveOwned(owned));
            Equal("replacement", File.ReadAllText(fixture.Record));
            True(File.Exists(fixture.Record + "-original"));
        });
        Test("quarantine mutation is restored, not deleted", () =>
        {
            using var fixture = new Scratch();
            using var registry = fixture.Open();
            using var owned = registry.Publish(Descriptor());
            registry.TestHook = point =>
            {
                if (point != "after-removal-move") return;
                File.WriteAllText(Directory.GetDirectories(fixture.Registry).Single() + "/record", "changed");
            };
            True(!registry.RemoveOwned(owned));
            Equal("changed", File.ReadAllText(fixture.Record));
        });
        Test("restore collision preserves replacement and quarantine", () =>
        {
            using var fixture = new Scratch();
            using var registry = fixture.Open();
            using var owned = registry.Publish(Descriptor());
            registry.TestHook = point =>
            {
                if (point != "after-removal-move") return;
                File.WriteAllText(Directory.GetDirectories(fixture.Registry).Single() + "/record", "quarantined");
                fixture.Write(Encoding.UTF8.GetBytes("replacement"));
            };
            Reject(() => registry.RemoveOwned(owned));
            Equal("replacement", File.ReadAllText(fixture.Record));
            Equal("quarantined", File.ReadAllText(Directory.GetDirectories(fixture.Registry).Single() + "/record"));
        });
        Test("receipt cannot authorize another registry or survive disposal", () =>
        {
            using var fixture = new Scratch();
            using var first = fixture.Open();
            using var second = fixture.Open();
            using var owned = first.Publish(Descriptor());
            Reject(() => second.RemoveOwned(owned));
            owned.Dispose();
            Reject(() => first.RemoveOwned(owned));
        });
        Test("directory metadata drift fails every operation without repair", () =>
        {
            using var fixture = new Scratch();
            using var registry = fixture.Open();
            using var owned = registry.Publish(Descriptor());
            File.SetUnixFileMode(fixture.Registry, (UnixFileMode)493);
            Reject(() => registry.ReadRecord(Descriptor().RecordName, new DiscoveryBudget()));
            Reject(() => registry.EnumerateNames(new DiscoveryBudget()));
            Reject(() => registry.Publish(Descriptor("b")));
            Reject(() => registry.RemoveOwned(owned));
            Equal((UnixFileMode)493, File.GetUnixFileMode(fixture.Registry));
        });
        Test("directory and record work ceilings fail whole operation", () =>
        {
            using var fixture = new Scratch();
            using var registry = fixture.Open();
            for (int i = 0; i < DiscoveryLimits.DirectoryEntries; i++) File.WriteAllText(fixture.Registry + "/unrelated-" + i, "");
            Equal(DiscoveryLimits.DirectoryEntries, registry.EnumerateNames(new DiscoveryBudget()).Length);
            File.WriteAllText(fixture.Registry + "/overflow", "");
            Reject(() => registry.EnumerateNames(new DiscoveryBudget()), DiscoveryError.LimitExceeded);
            var budget = new DiscoveryBudget();
            for (int i = 0; i < DiscoveryLimits.Records; i++) Reject(() => registry.ReadRecord(Descriptor().RecordName, budget));
            Reject(() => registry.ReadRecord(Descriptor().RecordName, budget), DiscoveryError.LimitExceeded);
        });
        Test("native enumeration handles maximum UTF8 names without trusting dirent type", () =>
        {
            using var fixture = new Scratch();
            using var registry = fixture.Open();
            string name = new string('雪', 85); // Exactly 255 UTF-8 bytes.
            File.WriteAllText(fixture.Registry + "/" + name, "");
            True(registry.EnumerateNames(new DiscoveryBudget()).Single() == name);
        });
        Test("invalid UTF8 entry fails enumeration without a partial list", () =>
        {
            using var fixture = new Scratch();
            using var registry = fixture.Open();
            byte[] path = Encoding.UTF8.GetBytes(fixture.Registry + "/").Concat(new byte[] { 255, 0 }).ToArray();
            int fd = open(path, 64 | 128 | 1 | 524288, 384);
            True(fd >= 0); True(close(fd) == 0);
            try { Reject(() => registry.EnumerateNames(new DiscoveryBudget())); }
            finally { True(unlink(path) == 0); }
        });
        Test("repeat publication/removal/disposal does not leak native descriptors", () =>
        {
            using var fixture = new Scratch();
            // Warm up runtime/PInvoke paths before counting fd table.
            using (var warm = fixture.Open()) using (var owned = warm.Publish(Descriptor())) True(warm.RemoveOwned(owned));
            int before = Directory.GetFiles("/proc/self/fd").Length;
            for (int i = 0; i < 50; i++)
            {
                using var registry = fixture.Open();
                using var owned = registry.Publish(Descriptor());
                Reject(() => registry.Publish(Descriptor()));
                registry.EnumerateNames(new DiscoveryBudget());
                True(registry.RemoveOwned(owned));
            }
            Equal(before, Directory.GetFiles("/proc/self/fd").Length);
            True(Directory.GetFileSystemEntries(fixture.Registry).Length == 0);
        });
        foreach (string mask in new[] { "000", "077", "777-directory", "777-file" })
            Test("isolated child creation under umask " + mask, () => RunChild(mask));
        Console.WriteLine($"Native Linux x64 filesystem tests: {passed} passed, {failed} failed (not Mono/game proof).");
        return failed;
    }

    internal static int RunUmaskChild(string mask)
    {
        using var fixture = new Scratch();
        uint previous = umask(0);
        try
        {
            if (mask == "777-directory")
            {
                umask(511);
                Reject(() => fixture.Open());
                True(!Directory.Exists(fixture.Registry));
                return 0;
            }
            using var registry = fixture.Open();
            umask(mask == "077" ? 63u : 0u);
            bool sawEmptyPrivateFile = false;
            registry.TestHook = point =>
            {
                if (point == "private-workspace-created" && mask == "777-file") umask(511);
                if (point != "private-file-created") return;
                string staged = Directory.GetDirectories(fixture.Registry).Single() + "/record";
                Equal(0L, new FileInfo(staged).Length);
                Equal(mask == "777-file" ? (UnixFileMode)0 : PrivateFile, File.GetUnixFileMode(staged));
                sawEmptyPrivateFile = true;
            };
            if (mask == "777-file")
            {
                Reject(() => registry.Publish(Descriptor()));
                True(!File.Exists(fixture.Record));
            }
            else
            {
                using var owned = registry.Publish(Descriptor());
                Equal(PrivateFile, File.GetUnixFileMode(fixture.Record));
                True(registry.RemoveOwned(owned));
            }
            True(sawEmptyPrivateFile);
            return 0;
        }
        finally { umask(previous); }
    }

    private static void RunChild(string mask)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardError = true, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(info.FileName) == "dotnet") info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("native-fs-umask"); info.ArgumentList.Add(mask);
        using var process = Process.Start(info)!;
        if (!process.WaitForExit(15000)) { process.Kill(true); process.WaitForExit(); throw new Exception("Owned umask child timed out."); }
        if (process.ExitCode != 0) throw new Exception("Owned umask child failed: " + process.StandardError.ReadToEnd());
    }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; }
        catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static void Reject(Action action, DiscoveryError code = DiscoveryError.RuntimeUnavailable)
    {
        try { action(); }
        catch (DiscoveryException error)
        {
            Equal(code, error.Code); Equal("Session discovery rejected.", error.Message); True(error.InnerException == null); return;
        }
        throw new Exception("Unsafe native operation accepted.");
    }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }

    private sealed class Scratch : IDisposable
    {
        internal readonly string Root = "/tmp/rim-pilot-native-tests-" + Guid.NewGuid().ToString("N");
        internal string Base => Root + "/runtime";
        internal string Registry => Base + "/rim-pilot/v1";
        internal string Record => Registry + "/" + Descriptor().RecordName;
        internal Scratch() { Directory.CreateDirectory(Base, PrivateDirectory); File.SetUnixFileMode(Root, PrivateDirectory); }
        internal LinuxPrivateDirectory Open() => LinuxPrivateDirectory.TestOpen(Base);
        internal void Write(byte[] bytes) { File.WriteAllBytes(Record, bytes); File.SetUnixFileMode(Record, PrivateFile); }
        public void Dispose() { RepairForCleanup(Root); Directory.Delete(Root, true); }
        private static void RepairForCleanup(string path)
        {
            if (new FileInfo(path).LinkTarget != null) return;
            if (Directory.Exists(path))
            {
                File.SetUnixFileMode(path, PrivateDirectory);
                foreach (string child in Directory.EnumerateFileSystemEntries(path)) RepairForCleanup(child);
            }
        }
    }
    [DllImport("libc", SetLastError = true)] private static extern int mkfifo(string path, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int link(string source, string target);
    [DllImport("libc")] private static extern uint umask(uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int open(byte[] path, int flags, uint mode);
    [DllImport("libc")] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int unlink(byte[] path);
}
