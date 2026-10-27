using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RimPilot.Protocol
{
    // Linux x86-64/glibc only. All names below an opened directory are single basenames.
    // No managed path-based I/O, chmod repair, recursive deletion or stale-record adoption.
    internal sealed partial class LinuxPrivateDirectory : IDisposable
    {
        private readonly object gate = new object();
        private readonly List<DirectoryNode> chain;
        private readonly uint uid;
        private bool disposed;
        private NativeFd Directory => chain[chain.Count - 1].Fd;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private LinuxPrivateDirectory(List<DirectoryNode> chain, uint uid) { this.chain = chain; this.uid = uid; }

        internal static uint EffectiveUid
        {
            get
            {
                RequirePlatform();
                return Native.geteuid();
            }
        }

        // Select once, retain that exact fd chain. Never reselect after an operation fails.
        internal static LinuxPrivateDirectory Open(string? xdgRuntimeDirectory)
        {
            try
            {
                uint uid = EffectiveUid;
                LinuxPrivateDirectory? opened = null;
                RuntimeDirectoryPolicy.Select(xdgRuntimeDirectory, uid, candidate =>
                {
                    opened = OpenCandidate(candidate, uid);
                    return true;
                });
                return opened!;
            }
            catch (DllNotFoundException) { throw Failure(); }
            catch (EntryPointNotFoundException) { throw Failure(); }
            catch (BadImageFormatException) { throw Failure(); }
        }

        private static void RequirePlatform()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                || RuntimeInformation.ProcessArchitecture != Architecture.X64 || !BitConverter.IsLittleEndian
                || Marshal.SizeOf(typeof(NativeStat)) != 144) throw Failure();
        }

        private static LinuxPrivateDirectory OpenCandidate(RuntimeDirectory candidate, uint uid)
        {
            var nodes = new List<DirectoryNode>();
            try
            {
                var root = new NativeFd(Native.openat(Native.AtCwd, PathBytes("/"), Native.DirectoryFlags, 0));
                nodes.Add(new DirectoryNode(root, "", false));
                CheckAncestor(Stat(root), uid, true);
                string[] parts = candidate.BasePath.Substring(1).Split('/');
                for (int i = 0; i < parts.Length; i++)
                {
                    bool last = i == parts.Length - 1;
                    if (candidate.IsFallback && last) MakeDirectory(nodes[nodes.Count - 1].Fd, parts[i]);
                    var fd = OpenDirectory(nodes[nodes.Count - 1].Fd, parts[i]);
                    nodes.Add(new DirectoryNode(fd, parts[i], last));
                    if (last) CheckPrivateDirectory(Stat(fd), uid);
                    else CheckAncestor(Stat(fd), uid, false);
                    // Fallback is anchored to genuine root-owned sticky /tmp, not TMPDIR.
                    if (candidate.IsFallback && i == 0)
                    {
                        NativeStat tmp = Stat(fd);
                        if (parts[i] != "tmp" || tmp.Uid != 0 || (tmp.Mode & 4095) != 1023) throw Failure();
                    }
                }
                foreach (string part in candidate.IsFallback ? new[] { "v1" } : new[] { "rim-pilot", "v1" })
                {
                    MakeDirectory(nodes[nodes.Count - 1].Fd, part);
                    NativeFd fd = OpenDirectory(nodes[nodes.Count - 1].Fd, part);
                    nodes.Add(new DirectoryNode(fd, part, true));
                    CheckPrivateDirectory(Stat(fd), uid);
                }
                var result = new LinuxPrivateDirectory(nodes, uid);
                result.ValidateChain();
                return result;
            }
            catch
            {
                foreach (DirectoryNode node in nodes) node.Fd.Dispose();
                throw;
            }
        }

        internal string[] EnumerateNames(DiscoveryBudget budget)
        {
            lock (gate)
            {
                ValidateChain();
                // New open file description: enumeration never shares/reuses another reader's offset.
                using (NativeFd fd = OpenDirectory(Directory, "."))
                {
                    IntPtr stream = Native.fdopendir(fd);
                    if (stream == IntPtr.Zero) throw Failure();
                    fd.SetHandleAsInvalid(); // fdopendir owns it from here, including errors below.
                    try
                    {
                        var names = new List<string>();
                        while (true)
                        {
                            Marshal.WriteInt32(Native.__errno_location(), 0);
                            IntPtr entry = Native.readdir(stream);
                            if (entry == IntPtr.Zero)
                            {
                                if (Marshal.ReadInt32(Native.__errno_location()) != 0) throw Failure();
                                break;
                            }
                            // glibc x86-64 struct dirent: reclen at 16, name at 19, maximum NAME_MAX=255.
                            int length = (ushort)Marshal.ReadInt16(entry, 16) - 19;
                            if (length < 1 || length > 261) throw Failure();
                            var bytes = new byte[length];
                            Marshal.Copy(IntPtr.Add(entry, 19), bytes, 0, length);
                            int end = Array.IndexOf(bytes, (byte)0);
                            if (end < 0 || end > 255) throw Failure();
                            string name;
                            try { name = Utf8.GetString(bytes, 0, end); }
                            catch (DecoderFallbackException) { throw Failure(); }
                            if (name == "." || name == "..") continue;
                            budget.ExamineEntry(); // Includes unrelated entries; one overflow lookahead only.
                            names.Add(name);
                        }
                        ValidateChain();
                        return names.ToArray();
                    }
                    finally { Native.closedir(stream); }
                }
            }
        }

        internal DiscoveryDescriptor ReadRecord(string name, DiscoveryBudget budget)
        {
            lock (gate)
            {
                budget.ReadRecord();
                CheckRecordName(name);
                ValidateChain();
                Observe("before-record-open");
                using (NativeFd fd = OpenRecord(Directory, name))
                {
                    NativeStat before = Stat(fd);
                    CheckRecord(before, uid);
                    Observe("before-record-read");
                    byte[] bytes = ReadBounded(fd, before);
                    CheckRecord(Stat(fd), uid);
                    if (!SameContentMetadata(before, Stat(fd))) throw Failure();
                    CheckEntry(Directory, name, fd);
                    ValidateChain();
                    return DiscoveryDescriptor.Read(bytes, name);
                }
            }
        }

        // Receipt pins original inode against reuse and owns a private empty staging/quarantine dir.
        // Publish never replaces an existing file, link or directory, even under a name collision.
        internal OwnedRecord Publish(DiscoveryDescriptor descriptor)
        {
            lock (gate)
            {
                ValidateChain();
                byte[] bytes = descriptor.EncodePrivate();
                Workspace work = NewWorkspace();
                NativeFd? file = null;
                bool published = false;
                try
                {
                    Observe("private-workspace-created");
                    file = new NativeFd(Native.openat(work.Fd, PathBytes("record"), Native.FileCreateFlags, 384));
                    Observe("private-file-created");
                    CheckRecord(Stat(file), uid);
                    if (Stat(file).Size != 0) throw Failure();
                    ValidateChain();
                    CheckWorkspace(work);
                    Observe("before-secret-write");
                    CheckRecord(Stat(file), uid);
                    WriteAll(file, bytes);
                    if (Native.fsync(file) != 0) throw Failure();
                    NativeStat written = Stat(file);
                    CheckRecord(written, uid);
                    if (written.Size != bytes.Length) throw Failure();
                    CheckEntry(work.Fd, "record", file);
                    Observe("before-publication");
                    ValidateChain();
                    CheckWorkspace(work);
                    CheckRecord(Stat(file), uid);
                    if (!SameContentMetadata(written, Stat(file))) throw Failure();
                    CheckEntry(work.Fd, "record", file);
                    var receipt = new OwnedRecord(this, descriptor.RecordName, bytes, file, work, written);
                    RenameNoReplace(work.Fd, "record", Directory, descriptor.RecordName);
                    published = true;
                    // No allocation/I/O after publication. Rename changes ctime, not content mtime.
                    return receipt;
                }
                finally
                {
                    if (!published)
                    {
                        // Only delete known file inside our pinned private workspace. Unexpected
                        // metadata/name replacements are retained, never recursively cleaned up.
                        if (file != null) TryUnlinkExact(work, file);
                        file?.Dispose();
                        ReleaseWorkspace(work);
                    }
                }
            }
        }

        internal bool RemoveOwned(OwnedRecord record)
        {
            lock (gate)
            {
                ValidateChain();
                if (!ReferenceEquals(record.Owner, this) || record.Closed || record.RemovalAttempted) throw Failure();
                CheckWorkspace(record.Work);
                // Validate BEFORE moving, then AGAIN in our private quarantine. There is no Linux
                // compare-inode-and-unlink syscall; check-then-unlink in registry is not sufficient.
                if (!MatchesOwned(Directory, record.Name, record)) return false;
                Observe("before-removal-move");
                int moved = Native.renameat2(Directory, PathBytes(record.Name), record.Work.Fd, PathBytes("record"), 1);
                if (moved != 0)
                {
                    if (Marshal.GetLastWin32Error() == Native.NoEntry) return false;
                    throw Failure();
                }
                record.RemovalAttempted = true;
                bool removed = false;
                try
                {
                    Observe("after-removal-move");
                    ValidateChain();
                    CheckWorkspace(record.Work);
                    if (MatchesOwned(record.Work.Fd, "record", record))
                    {
                        if (Native.unlinkat(record.Work.Fd, PathBytes("record"), 0) != 0) throw Failure();
                        removed = true;
                        return true;
                    }
                    return false;
                }
                finally
                {
                    // A raced replacement is restored only into an empty original name. If another
                    // writer filled it, retain quarantine and fail; never overwrite/delete either.
                    if (!removed) RenameNoReplace(record.Work.Fd, "record", Directory, record.Name);
                }
            }
        }

        private bool MatchesOwned(NativeFd parent, string name, OwnedRecord record)
        {
            try
            {
                using (NativeFd fd = OpenRecord(parent, name))
                {
                    NativeStat stat = Stat(fd);
                    CheckRecord(stat, uid);
                    if (!SameInode(stat, Stat(record.File)) || !SameOwnedMetadata(record.Published, stat)) return false;
                    byte[] bytes = ReadBounded(fd, stat);
                    if (bytes.Length != record.Bytes.Length) return false;
                    int difference = 0;
                    for (int i = 0; i < bytes.Length; i++) difference |= bytes[i] ^ record.Bytes[i];
                    CheckRecord(Stat(fd), uid);
                    return difference == 0 && SameContentMetadata(stat, Stat(fd));
                }
            }
            catch (DiscoveryException) { return false; }
        }

        private static byte[] ReadBounded(NativeFd fd, NativeStat before)
        {
            if (before.Size < 1 || before.Size > DiscoveryLimits.DescriptorBytes) throw Failure();
            var buffer = new byte[DiscoveryLimits.DescriptorBytes + 1];
            long count = Native.pread(fd, buffer, (UIntPtr)buffer.Length, 0);
            // Short/interrupted, grown or shrunk reads fail closed; no indefinite signal retries.
            if (count != before.Size) throw Failure();
            var sentinel = new byte[1];
            if (Native.pread(fd, sentinel, (UIntPtr)1, count) != 0) throw Failure();
            var result = new byte[(int)count];
            Buffer.BlockCopy(buffer, 0, result, 0, result.Length);
            return result;
        }

        private static void WriteAll(NativeFd fd, byte[] bytes)
        {
            // <=1024 bytes, one bounded native regular-file write. Short/interrupted writes fail
            // closed and staging is not published; no indefinite EINTR retry on hostile signals.
            if (Native.write(fd, bytes, (UIntPtr)bytes.Length) != bytes.Length) throw Failure();
        }

        private Workspace NewWorkspace()
        {
            string name = ".pending-" + Ids.NewToken();
            // Exclusive mkdir: no existing workspace may be adopted.
            if (Native.mkdirat(Directory, PathBytes(name), 448) != 0) throw Failure();
            NativeFd fd = OpenDirectory(Directory, name);
            var work = new Workspace(name, fd);
            try { CheckWorkspace(work); return work; }
            catch { fd.Dispose(); throw; }
        }

        private void CheckWorkspace(Workspace work)
        {
            CheckPrivateDirectory(Stat(work.Fd), uid);
            using (NativeFd entry = OpenDirectory(Directory, work.Name))
                if (!SameInode(Stat(work.Fd), Stat(entry))) throw Failure();
        }

        private void TryUnlinkExact(Workspace work, NativeFd file)
        {
            try
            {
                ValidateChain();
                CheckWorkspace(work);
                CheckEntry(work.Fd, "record", file);
                // Never follow file on cleanup; only our private staging basename is unlinked.
                Native.unlinkat(work.Fd, PathBytes("record"), 0);
            }
            catch (DiscoveryException) { }
        }

        private void ReleaseWorkspace(Workspace work)
        {
            try
            {
                if (!disposed)
                {
                    ValidateChain();
                    CheckWorkspace(work);
                    Native.unlinkat(Directory, PathBytes(work.Name), Native.RemoveDirectory); // empty only
                }
            }
            catch (DiscoveryException) { }
            finally { work.Fd.Dispose(); }
        }

        private void ValidateChain()
        {
            if (disposed || Native.geteuid() != uid) throw Failure();
            for (int i = 0; i < chain.Count; i++)
            {
                DirectoryNode node = chain[i];
                NativeStat stat = Stat(node.Fd);
                if (node.Private) CheckPrivateDirectory(stat, uid);
                else CheckAncestor(stat, uid, i == 0);
                if (i != 0)
                {
                    using (NativeFd current = OpenDirectory(chain[i - 1].Fd, node.Name))
                        if (!SameInode(stat, Stat(current))) throw Failure();
                }
            }
        }

        private static void CheckAncestor(NativeStat stat, uint uid, bool root)
        {
            if ((stat.Mode & 61440) != 16384 || stat.Links == 0 || (root ? stat.Uid != 0 : stat.Uid != 0 && stat.Uid != uid))
                throw Failure();
            uint mode = stat.Mode & 4095;
            if ((mode & 18) != 0 && !(stat.Uid == 0 && mode == 1023 && !root)) throw Failure();
            if ((mode & 3072) != 0) throw Failure();
        }

        private static void CheckPrivateDirectory(NativeStat stat, uint uid)
        {
            if ((stat.Mode & 61440) != 16384 || (stat.Mode & 4095) != 448 || stat.Uid != uid || stat.Links == 0)
                throw Failure();
        }

        private static void CheckRecord(NativeStat stat, uint uid)
        {
            if ((stat.Mode & 61440) != 32768 || (stat.Mode & 4095) != 384 || stat.Uid != uid || stat.Links != 1)
                throw Failure();
        }

        private static void CheckEntry(NativeFd parent, string name, NativeFd expected)
        {
            using (NativeFd entry = OpenRecord(parent, name))
                if (!SameInode(Stat(expected), Stat(entry))) throw Failure();
        }

        private static bool SameInode(NativeStat a, NativeStat b) => a.Device == b.Device && a.Inode == b.Inode;
        private static bool SameOwnedMetadata(NativeStat a, NativeStat b) => SameInode(a, b)
            && a.Mode == b.Mode && a.Uid == b.Uid && a.Gid == b.Gid && a.Links == b.Links && a.Size == b.Size
            && a.ModifiedSeconds == b.ModifiedSeconds && a.ModifiedNanos == b.ModifiedNanos;
        private static bool SameContentMetadata(NativeStat a, NativeStat b) => SameOwnedMetadata(a, b)
            && a.ChangedSeconds == b.ChangedSeconds && a.ChangedNanos == b.ChangedNanos;

        private static NativeStat Stat(NativeFd fd)
        {
            if (fd.IsInvalid || fd.IsClosed || Native.fstat(fd, out NativeStat stat) != 0) throw Failure();
            return stat;
        }

        private static NativeFd OpenDirectory(NativeFd parent, string name) =>
            new NativeFd(Native.openat(parent, PathBytes(name), Native.DirectoryFlags, 0));
        private static NativeFd OpenRecord(NativeFd parent, string name)
        {
            // O_PATH examines type without opening a device/FIFO for I/O. The subsequent
            // no-follow/nonblocking open must resolve to that same pinned regular inode.
            using (var metadata = new NativeFd(Native.openat(parent, PathBytes(name), Native.MetadataFlags, 0)))
            {
                NativeStat expected = Stat(metadata);
                if ((expected.Mode & 61440) != 32768) throw Failure();
                var fd = new NativeFd(Native.openat(parent, PathBytes(name), Native.RecordFlags, 0));
                try
                {
                    if (!SameInode(expected, Stat(fd))) throw Failure();
                    return fd;
                }
                catch { fd.Dispose(); throw; }
            }
        }
        private static void MakeDirectory(NativeFd parent, string name)
        {
            if (Native.mkdirat(parent, PathBytes(name), 448) != 0 && Marshal.GetLastWin32Error() != Native.Exists) throw Failure();
        }
        private static void RenameNoReplace(NativeFd from, string source, NativeFd to, string target)
        {
            try
            {
                if (Native.renameat2(from, PathBytes(source), to, PathBytes(target), 1) != 0) throw Failure();
            }
            catch (EntryPointNotFoundException) { throw Failure(); }
        }
        private static byte[] PathBytes(string value) => Utf8.GetBytes(value + "\0");
        private static void CheckRecordName(string name)
        {
            if (name == null || name.Length != 39 || !name.EndsWith(".json", StringComparison.Ordinal)
                || !Ids.Is(name.Substring(0, 34), "s")) throw Failure();
        }
        private static DiscoveryException Failure() => new DiscoveryException(DiscoveryError.RuntimeUnavailable);
        public override string ToString() => "Private native registry.";

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                foreach (DirectoryNode node in chain) node.Fd.Dispose();
            }
        }

        // Compiled out in production. Tests provide implementation in their own assembly only.
        partial void Observe(string point);

        internal sealed class OwnedRecord : IDisposable
        {
            internal readonly LinuxPrivateDirectory Owner;
            internal readonly string Name;
            internal readonly byte[] Bytes;
            internal readonly NativeFd File;
            internal readonly Workspace Work;
            internal readonly NativeStat Published;
            internal bool RemovalAttempted;
            internal bool Closed;
            internal OwnedRecord(LinuxPrivateDirectory owner, string name, byte[] bytes, NativeFd file, Workspace work, NativeStat published)
            { Owner = owner; Name = name; Bytes = bytes; File = file; Work = work; Published = published; }
            public override string ToString() => "Private owned record.";
            // Dispose releases handles, not publication. Explicit RemoveOwned is required; death or
            // uncertain ownership must leave a stale record, never imply authority to delete it.
            public void Dispose()
            {
                lock (Owner.gate)
                {
                    if (Closed) return;
                    Closed = true;
                    File.Dispose();
                    Owner.ReleaseWorkspace(Work);
                }
            }
        }

        private sealed class DirectoryNode
        {
            internal readonly NativeFd Fd;
            internal readonly string Name;
            internal readonly bool Private;
            internal DirectoryNode(NativeFd fd, string name, bool isPrivate) { Fd = fd; Name = name; Private = isPrivate; }
        }
        internal sealed class Workspace
        {
            internal readonly string Name;
            internal readonly NativeFd Fd;
            internal Workspace(string name, NativeFd fd) { Name = name; Fd = fd; }
        }
        internal sealed class NativeFd : SafeHandleMinusOneIsInvalid
        {
            internal NativeFd(int fd) : base(true) { SetHandle((IntPtr)fd); if (fd < 0) throw Failure(); }
            // Linux close releases fd even on EINTR; retry could close an unrelated reused fd.
            protected override bool ReleaseHandle() => Native.close(handle.ToInt32()) == 0;
        }

        // glibc Linux x86-64 struct stat, verified against installed public headers + C ABI test.
        [StructLayout(LayoutKind.Explicit, Size = 144)]
        internal struct NativeStat
        {
            [FieldOffset(0)] internal ulong Device;
            [FieldOffset(8)] internal ulong Inode;
            [FieldOffset(16)] internal ulong Links;
            [FieldOffset(24)] internal uint Mode;
            [FieldOffset(28)] internal uint Uid;
            [FieldOffset(32)] internal uint Gid;
            [FieldOffset(48)] internal long Size;
            [FieldOffset(88)] internal long ModifiedSeconds;
            [FieldOffset(96)] internal long ModifiedNanos;
            [FieldOffset(104)] internal long ChangedSeconds;
            [FieldOffset(112)] internal long ChangedNanos;
        }

        private static class Native
        {
            internal const int AtCwd = -100, NoEntry = 2, Exists = 17, RemoveDirectory = 512;
            // O_CLOEXEC | O_NOFOLLOW | O_DIRECTORY | O_RDONLY
            internal const int DirectoryFlags = 524288 | 131072 | 65536;
            // O_PATH | O_CLOEXEC | O_NOFOLLOW: inspect without activating special files.
            internal const int MetadataFlags = 2097152 | 524288 | 131072;
            // O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK | O_NOCTTY | O_RDONLY
            internal const int RecordFlags = 524288 | 131072 | 2048 | 256;
            // O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK | O_NOCTTY | O_CREAT | O_EXCL | O_RDWR
            internal const int FileCreateFlags = RecordFlags | 64 | 128 | 2;
            private const string Libc = "libc";
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl)] internal static extern uint geteuid();
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)] internal static extern int openat(int dir, byte[] name, int flags, uint mode);
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)] internal static extern int openat(NativeFd dir, byte[] name, int flags, uint mode);
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)] internal static extern int mkdirat(NativeFd dir, byte[] name, uint mode);
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)] internal static extern int fstat(NativeFd fd, out NativeStat stat);
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)] internal static extern long pread(NativeFd fd, [Out] byte[] bytes, UIntPtr count, long offset);
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)] internal static extern long write(NativeFd fd, byte[] bytes, UIntPtr count);
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)] internal static extern int fsync(NativeFd fd);
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)] internal static extern int renameat2(NativeFd from, byte[] source, NativeFd to, byte[] target, uint flags);
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)] internal static extern int unlinkat(NativeFd dir, byte[] name, int flags);
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl)] internal static extern int close(int fd);
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)] internal static extern IntPtr fdopendir(NativeFd fd);
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl, SetLastError = true)] internal static extern IntPtr readdir(IntPtr stream);
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl)] internal static extern int closedir(IntPtr stream);
            [DllImport(Libc, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr __errno_location();
        }
    }
}
