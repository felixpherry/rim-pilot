using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;

namespace RimPilot.Mod.Observation
{
    // Trusted adapter for one snapshot's source, fixed at build; never client data.
    internal interface ISnapshotSource
    {
        // Required structural witness (membership/order source version). Any change after the
        // build started expires the snapshot and all its cursors. False = source unavailable.
        bool TryStructure(out long version);
        // Optional audited eligibility revision that advances on ANY change that could alter a
        // member's eligibility (fog, concealment, ownership, spawn, map access). False = none:
        // whole-vector revalidation must then finish inside a single update.
        bool TryEligibility(out long revision);
        // Rerun every audited gate for one retained member, charging its own nested work first.
        // Deferred = budget yielded/failed; never treated as ineligible.
        TargetGateResult Revalidate(object member, INestedWork work);
    }

    internal enum Revalidated { Valid, Continue, Expired }

    // Monotonic edit counter fed by audited hooks (any thread). Unavailable until every hook
    // was installed; a missing hook is never treated as "no edits".
    internal sealed class EditCounter
    {
        // Fog bit writers: FogGrid unfog/refog/all-fogged/load/dispose (docs/visibility-audit.md).
        internal static readonly EditCounter Fog = new EditCounter();
        private long edits;
        private int installed;
        internal void MarkInstalled() => Volatile.Write(ref installed, 1);
        internal void Bump() => Interlocked.Increment(ref edits);
        internal bool TryRead(out long value)
        {
            value = Interlocked.Read(ref edits);
            return Volatile.Read(ref installed) == 1;
        }
    }

    // Eligibility revision for one list source: a shared edit counter for state no member
    // witness covers (fog), plus a per-member witness recorded at that member's latest gate
    // decision (eligible or not). Any shared edit or changed/unreadable witness advances the
    // revision and forgets every witness, so a pass with an unchanged revision proves each
    // member's gate inputs were unchanged from its own check until the end of the pass.
    // Witness comparison reads only fixed trusted plain fields: at most SnapshotItems members.
    internal sealed class MemberWitnesses<TState>
    {
        private readonly EditCounter shared;
        private readonly Func<object, TState, bool> unchanged;
        private readonly Dictionary<object, TState> states = new Dictionary<object, TState>(IdentityComparer.Instance);
        private long revision, seen;
        private bool started;
        internal int Count => states.Count;
        internal MemberWitnesses(EditCounter shared, Func<object, TState, bool> unchanged)
        { this.shared = shared; this.unchanged = unchanged; }

        // False when the bound is reached: caller must fail/omit, never skip the witness.
        internal bool Record(object member, TState state)
        {
            if (member == null || (states.Count >= WireLimits.SnapshotItems && !states.ContainsKey(member))) return false;
            states[member] = state;
            return true;
        }

        internal bool TryRevision(out long value)
        {
            value = 0;
            if (!shared.TryRead(out long edits)) return false;
            bool changed = started && edits != seen;
            seen = edits; started = true;
            if (!changed)
                foreach (KeyValuePair<object, TState> entry in states)
                {
                    bool same;
                    try { same = unchanged(entry.Key, entry.Value); }
                    catch { same = false; }
                    if (!same) { changed = true; break; }
                }
            if (changed) { states.Clear(); revision++; }
            value = revision;
            return true;
        }
    }

    // Structural witness over one trusted List<T> source: Mono's List<T>.Enumerator rejects
    // any Add/Remove/Clear/indexer write/Sort after capture, even with identical final refs.
    // Latches: once changed, never "unchanged" again. A replaced list reference also latches.
    internal sealed class ListWitness<T>
    {
        private readonly List<T> list;
        private List<T>.Enumerator version;
        private bool changed;
        internal List<T> List => list;
        internal ListWitness(List<T> list) { this.list = list ?? throw new ArgumentNullException(nameof(list)); version = list.GetEnumerator(); }
        internal bool Unchanged(List<T>? current)
        {
            if (changed) return false;
            if (!ReferenceEquals(current, list)) { changed = true; return false; }
            try { version.MoveNext(); }
            catch (InvalidOperationException) { changed = true; }
            return !changed;
        }
    }

    // Frozen eligible DTO vector. Values are historical: revalidation proves current
    // eligibility of every member but never rewrites, drops or reorders a row.
    internal sealed class EligibleSnapshot
    {
        internal readonly string Id = Ids.New("q");
        internal readonly Scope Scope;
        internal readonly ReadMethod Method;
        internal readonly int Limit, Bytes;
        internal readonly ISnapshotSource Source;
        internal readonly long Structure;
        internal readonly JObject[] Items;
        internal readonly object[] Members;
        // Lazily allocated once per page boundary (index 0 = first page, no cursor needed).
        internal readonly string?[] Cursors;
        internal long Created;
        internal bool Live = true, Retained;
        internal EligibleSnapshot(Scope scope, ReadMethod method, int limit, JObject[] items, object[] members,
            int bytes, ISnapshotSource source, long structure)
        {
            Scope = scope; Method = method; Limit = limit; Items = items; Members = members;
            Bytes = bytes; Source = source; Structure = structure;
            Cursors = new string?[Math.Max(1, (items.Length + limit - 1) / limit)];
        }
    }

    // One whole-vector pass. Interval protection: either an unchanged audited eligibility
    // revision covers every slice, or the pass starts and ends in the same update.
    internal sealed class Revalidation
    {
        internal readonly EligibleSnapshot Snapshot;
        internal readonly long Generation;
        internal int Next;
        internal object? Update, Proven;
        internal bool Witnessed;
        internal long Eligibility;
        internal Revalidation(EligibleSnapshot snapshot, long generation) { Snapshot = snapshot; Generation = generation; }
    }

    // Game thread only. Bounded retained snapshots and reusable query-bound page cursors. No
    // raw index, count, owner or source detail is ever exposed: every miss is cursor_expired.
    internal sealed class SnapshotStore
    {
        private readonly Func<long> now;
        private readonly long lifetime;
        private readonly EligibleSnapshot?[] slots = new EligibleSnapshot?[WireLimits.Snapshots];
        private readonly Dictionary<string, KeyValuePair<EligibleSnapshot, int>> cursors =
            new Dictionary<string, KeyValuePair<EligibleSnapshot, int>>(StringComparer.Ordinal);
        private long generation;
        internal int Count { get { Purge(); int n = 0; foreach (var s in slots) if (s != null) n++; return n; } }
        internal int CursorCount => cursors.Count;

        internal SnapshotStore(Func<long>? now = null, int lifetimeMs = WireLimits.SnapshotLifetimeMs)
        {
            this.now = now ?? (() => Monotonic.Now);
            lifetime = (long)Math.Ceiling(lifetimeMs * (double)System.Diagnostics.Stopwatch.Frequency / 1000);
        }

        // Frozen but not yet retained: call after the bounded build finished. structure = source
        // witness read when the build STARTED. Retain only after a Valid whole-vector pass.
        internal EligibleSnapshot Create(Scope scope, ReadMethod method, int limit, JObject[] items, object[] members,
            int bytes, ISnapshotSource source, long structure)
        {
            if (scope == null || source == null || items == null || members == null || items.Length != members.Length
                || items.Length > WireLimits.SnapshotItems || limit < 1 || limit > WireLimits.PageItems
                || bytes < 0 || bytes > WireLimits.SnapshotBytes) throw new ArgumentException("Invalid snapshot.");
            foreach (JObject item in items) if (item == null) throw new ArgumentException("Invalid snapshot.");
            foreach (object member in members) if (member == null) throw new ArgumentException("Invalid snapshot.");
            return new EligibleSnapshot(scope, method, limit, (JObject[])items.Clone(), (object[])members.Clone(),
                bytes, source, structure) { Created = now() };
        }

        // Oldest retained snapshot is evicted (its cursors expire). False when already expired.
        internal bool Retain(Revalidation pass, object update)
        {
            EligibleSnapshot snapshot = pass.Snapshot;
            if (!Proven(pass, update)) return false;
            if (snapshot.Retained) return true;
            int slot = -1;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) { slot = i; break; }
                if (slot < 0 || slots[i]!.Created < slots[slot]!.Created) slot = i;
            }
            if (slots[slot] != null) Remove(slots[slot]!);
            slots[slot] = snapshot; snapshot.Retained = true;
            return true;
        }

        // Structural/authorization change: every snapshot, cursor and in-flight pass expires.
        internal void Expire()
        {
            for (int i = 0; i < slots.Length; i++) if (slots[i] != null) slots[i]!.Live = false;
            Array.Clear(slots, 0, slots.Length);
            cursors.Clear();
            generation++;
        }

        // Exact binding: same scope (including map), method and limit. Unknown, evicted,
        // timed-out and wrongly bound cursors are indistinguishable.
        internal bool TryResolve(Scope scope, ReadMethod method, int limit, string cursor,
            out EligibleSnapshot? snapshot, out int start)
        {
            snapshot = null; start = 0;
            Purge();
            if (scope == null || cursor == null || !Ids.Is(cursor, "q")
                || !cursors.TryGetValue(cursor, out KeyValuePair<EligibleSnapshot, int> entry)) return false;
            EligibleSnapshot found = entry.Key;
            if (!found.Live || !found.Scope.Same(scope) || found.Method != method || found.Limit != limit) return false;
            snapshot = found; start = entry.Value * found.Limit;
            return true;
        }

        internal Revalidation BeginRevalidation(EligibleSnapshot snapshot) => new Revalidation(snapshot, generation);

        // Charges one nested field per member before its gate. Continue = yielded/failed (the
        // caller retries next update); Expired = whole snapshot gone, no row diagnosis.
        internal Revalidated Revalidate(Revalidation pass, object update, INestedWork work)
        {
            EligibleSnapshot snapshot = pass.Snapshot;
            if (update == null || work == null || pass.Generation != generation || !Alive(snapshot)
                || !Structural(snapshot)) return Revalidated.Expired;
            bool witnessed = snapshot.Source.TryEligibility(out long revision);
            if (pass.Next > 0 && (pass.Witnessed ? !witnessed || revision != pass.Eligibility
                : !ReferenceEquals(pass.Update, update))) pass.Next = 0;
            pass.Proven = null;
            if (pass.Next == 0) { pass.Update = update; pass.Witnessed = witnessed; pass.Eligibility = revision; }
            while (pass.Next < snapshot.Members.Length)
            {
                if (!work.TryNestedFields(1)) return Revalidated.Continue;
                TargetGateResult result;
                try { result = snapshot.Source.Revalidate(snapshot.Members[pass.Next], work); }
                catch { result = TargetGateResult.Unavailable; }
                if (result == TargetGateResult.Deferred) return Revalidated.Continue;
                if (result != TargetGateResult.Eligible) { Remove(snapshot); return Revalidated.Expired; }
                pass.Next++;
            }
            if (!Structural(snapshot)) return Revalidated.Expired;
            bool interval = pass.Witnessed
                ? snapshot.Source.TryEligibility(out long end) && end == pass.Eligibility
                : ReferenceEquals(pass.Update, update);
            pass.Next = 0;
            if (!interval) return Revalidated.Continue;
            pass.Proven = update;
            return Revalidated.Valid;
        }

        // Publication requires a Valid whole-vector pass in this same update and generation.
        private bool Proven(Revalidation pass, object update) =>
            pass != null && update != null && ReferenceEquals(pass.Proven, update)
            && pass.Generation == generation && Alive(pass.Snapshot);

        // Page from an immutable start of a retained snapshot whose pass was proven this update.
        internal JObject Page(Revalidation pass, object update, int start, SampleStamp sample)
        {
            EligibleSnapshot snapshot = pass.Snapshot;
            if (!Proven(pass, update) || !snapshot.Retained || start < 0 || start % snapshot.Limit != 0
                || (start >= snapshot.Items.Length && start != 0)) throw new WireException(WireError.CursorExpired);
            int end = Math.Min(snapshot.Items.Length, start + snapshot.Limit);
            var items = new JArray();
            for (int i = start; i < end; i++) items.Add(snapshot.Items[i].DeepClone());
            string? next = null;
            if (end < snapshot.Items.Length)
            {
                int boundary = end / snapshot.Limit;
                next = snapshot.Cursors[boundary];
                if (next == null)
                {
                    next = snapshot.Cursors[boundary] = Ids.New("q");
                    cursors.Add(next, new KeyValuePair<EligibleSnapshot, int>(snapshot, boundary));
                }
            }
            return Responses.Page(sample, snapshot.Id, items, next);
        }

        private bool Structural(EligibleSnapshot snapshot)
        {
            bool same;
            try { same = snapshot.Source.TryStructure(out long version) && version == snapshot.Structure; }
            catch { same = false; }
            if (!same) Remove(snapshot);
            return same;
        }

        private bool Alive(EligibleSnapshot snapshot)
        {
            Purge();
            if (snapshot.Live && now() - snapshot.Created >= lifetime) Remove(snapshot);
            return snapshot.Live;
        }

        private void Purge()
        {
            long current = now();
            foreach (EligibleSnapshot? snapshot in slots)
                if (snapshot != null && current - snapshot.Created >= lifetime) Remove(snapshot);
        }

        private void Remove(EligibleSnapshot snapshot)
        {
            snapshot.Live = false;
            for (int i = 0; i < slots.Length; i++) if (ReferenceEquals(slots[i], snapshot)) slots[i] = null;
            foreach (string? cursor in snapshot.Cursors) if (cursor != null) cursors.Remove(cursor);
        }
    }
}
