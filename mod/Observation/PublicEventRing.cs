using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;

namespace RimPilot.Mod.Observation
{
    // Bounded primitive-only history. Game adapters own availability witnesses; no game
    // object, source index, loss count or internal sequence is ever serialized.
    internal sealed class PublicEventRing
    {
        private sealed class Entry
        {
            internal long Sequence;
            internal int Bytes;
            internal JObject Item = null!;
        }
        private sealed class Cursor
        {
            internal string Id = Ids.New("e");
            internal long After, Expires;
        }
        internal readonly object Sync = new object();
        private readonly List<Entry> entries = new List<Entry>();
        private readonly List<Cursor> cursors = new List<Cursor>();
        private readonly System.Collections.Generic.HashSet<long> retained = new System.Collections.Generic.HashSet<long>();
        private readonly Func<long> clock;
        private Scope? scope, lastScope;
        private long sequence, gap;
        private int bytes;
        private bool started;
        internal PublicEventRing(Func<long> clock) { this.clock = clock; }
        internal int Count => entries.Count;
        internal int Bytes => bytes;
        internal int CursorCount => cursors.Count;
        internal void Suspend()
        {
            entries.Clear(); cursors.Clear(); retained.Clear(); bytes = 0; scope = null; sequence = gap = 0;
        }
        internal void Ready(Scope current)
        {
            if (scope != null && scope.Same(current)) return;
            string text = !started ? "observation_ready" : lastScope != null && !lastScope.Same(current)
                ? "scope_changed" : "observation_resumed";
            Suspend(); scope = lastScope = current; started = true;
            Add("lifecycle", null, text);
        }
        internal long Publish(PublicationDisposition disposition, bool audited, bool first, bool safe,
            string kind, string? label, string? text)
        {
            if (scope == null || !PublicEventPolicy.CanPublish(disposition, audited, first, safe)
                || (kind != "letter" && kind != "message") || !Clean(text, 2048)
                || (label != null && !Clean(label, 512))) return 0;
            return Add(kind, label, text!);
        }
        private static bool Clean(string? text, int max)
        {
            if (string.IsNullOrEmpty(text) || text!.Length > max) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '<' || c == '>' || (char.IsControl(c) && c != '\n' && c != '\t')) return false;
                if (char.IsHighSurrogate(c)) { if (++i == text.Length || !char.IsLowSurrogate(text[i])) return false; }
                else if (char.IsLowSurrogate(c)) return false;
            }
            return true;
        }
        private long Add(string kind, string? label, string text)
        {
            var item = new JObject { ["kind"] = kind, ["text"] = text };
            if (label != null) item["label"] = label;
            int size = Encoding.UTF8.GetByteCount(item.ToString(Formatting.None));
            if (size > WireLimits.EventBytes || sequence == long.MaxValue) { Suspend(); return 0; }
            while (entries.Count >= WireLimits.EventItems || bytes + size > WireLimits.EventBytes) RemoveAt(0);
            entries.Add(new Entry { Sequence = ++sequence, Bytes = size, Item = item }); retained.Add(sequence); bytes += size;
            return sequence;
        }
        internal void Remove(long accepted)
        {
            for (int i = 0; i < entries.Count; i++) if (entries[i].Sequence == accepted) { RemoveAt(i); return; }
        }
        internal bool Contains(long accepted) => retained.Contains(accepted);
        private void RemoveAt(int index)
        {
            Entry e = entries[index]; gap = Math.Max(gap, e.Sequence); bytes -= e.Bytes; retained.Remove(e.Sequence); entries.RemoveAt(index);
        }
        internal JObject Read(Scope current, string? token, int limit, INestedWork work)
        {
            if (scope == null || !scope.Same(current)) throw new WireException(WireError.CursorExpired);
            if (limit < 1 || limit > WireLimits.PageItems) throw new WireException(WireError.InvalidRequest);
            if (!work.TryNestedFields(Math.Max(1, entries.Count + cursors.Count + Math.Min(entries.Count, limit))))
                throw new WireException(WireError.Unavailable);
            long now = clock();
            for (int i = cursors.Count - 1; i >= 0; i--) if (cursors[i].Expires <= now) cursors.RemoveAt(i);
            long after = entries.Count == 0 ? sequence : entries[0].Sequence - 1;
            if (token != null)
            {
                Cursor? found = null;
                foreach (Cursor cursor in cursors) if (cursor.Id == token) { found = cursor; break; }
                if (found == null) throw new WireException(WireError.CursorExpired);
                after = found.After;
                if (after < gap) throw new WireException(WireError.EventGap);
            }
            var items = new JArray(); int pageBytes = 0;
            foreach (Entry e in entries)
                if (e.Sequence > after)
                {
                    if (pageBytes + e.Bytes > WireLimits.ResponseBytes - 4096) break;
                    items.Add(e.Item.DeepClone()); pageBytes += e.Bytes; after = e.Sequence;
                    if (items.Count == limit) break;
                }
            Cursor? next = null;
            foreach (Cursor c in cursors) if (c.After == after) { next = c; next.Expires = now + WireLimits.EventCursorLifetimeMs; break; }
            if (next == null)
            {
                if (cursors.Count == WireLimits.EventCursors) cursors.RemoveAt(0);
                next = new Cursor { After = after, Expires = now + WireLimits.EventCursorLifetimeMs }; cursors.Add(next);
            }
            return new JObject { ["scope"] = current.Json(), ["items"] = items, ["next"] = next.Id };
        }
    }

    internal sealed class EventProducer<TMap> : IReadProducer<TMap> where TMap : class
    {
        private readonly PublicEventRing ring;
        private readonly Func<ProducerWork<TMap>, bool> prepare;
        internal EventProducer(PublicEventRing ring, Func<ProducerWork<TMap>, bool> prepare)
        { this.ring = ring; this.prepare = prepare; }
        public bool SnapshotBuild => false;
        public IReadContinuation<TMap> Start(Request request) => new Continuation(this);
        private sealed class Continuation : IReadContinuation<TMap>
        {
            private readonly EventProducer<TMap> owner;
            private JObject? result;
            internal Continuation(EventProducer<TMap> owner) { this.owner = owner; }
            public StepResult Step(ProducerWork<TMap> work) => StepResult.Complete;
            public bool Eligible(ProducerWork<TMap> work)
            {
                result = null; work.ReleaseMemory();
                lock (owner.ring.Sync)
                {
                    if (!owner.prepare(work)) return false;
                    try { result = owner.ring.Read(work.Scope, work.Request.Cursor, work.Request.Limit, work); }
                    catch (WireException) when (work.Yielded) { return false; }
                    return work.TryRetain(Encoding.UTF8.GetByteCount(result.ToString(Formatting.None)));
                }
            }
            public JObject Result() => result ?? throw new InvalidOperationException();
            public void Discard() { result = null; }
        }
    }
}
