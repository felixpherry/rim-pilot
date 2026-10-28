using System;
using RimPilot.Protocol;

namespace RimPilot.Mod.Observation
{
    // Fixed witness table, independent of archive size/pinning. Reference identity only.
    internal sealed class PublicEventWitnesses
    {
        private sealed class Identity : System.Collections.Generic.IEqualityComparer<object>
        {
            public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
        private sealed class Witness
        {
            internal object Source = null!, Acceptance = null!;
            internal object? Archive;
            internal long Event;
            internal bool Live, Letter;
        }
        private readonly Witness?[] slots = new Witness?[WireLimits.EventItems];
        private readonly PublicEventRing ring;
        internal PublicEventWitnesses(PublicEventRing ring) { this.ring = ring; }
        internal void Clear() { Array.Clear(slots, 0, slots.Length); }
        internal void Accepted(object acceptance, object source, object? archive, bool letter, string? label, string? text)
        {
            int free = -1;
            for (int i = 0; i < slots.Length; i++)
            {
                Witness? w = slots[i];
                if (w != null && !ring.Contains(w.Event)) slots[i] = w = null;
                if (w == null) { free = i; continue; }
                if (ReferenceEquals(w.Acceptance, acceptance)) return; // Dedup acceptance, never source/text forever.
            }
            long accepted = ring.Publish(PublicationDisposition.AcceptedPublic, true, true, true,
                letter ? "letter" : "message", label, text);
            if (accepted == 0) return;
            // Full ring just evicted oldest acceptance. Reuse that witness slot only after
            // successful filtering/publication, never let bad payload evict public history.
            if (free < 0)
            {
                free = 0;
                for (int i = 1; i < slots.Length; i++) if (slots[i]!.Event < slots[free]!.Event) free = i;
            }
            slots[free] = new Witness { Acceptance = acceptance, Source = source, Archive = archive, Letter = letter, Live = true, Event = accepted };
        }
        internal void RemovedFromArchive(object archive, object source)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                Witness? w = slots[i];
                if (w != null && ReferenceEquals(w.Source, source) && ReferenceEquals(w.Archive, archive))
                { w.Archive = null; Expire(i, w); }
            }
        }
        internal void RemovedLetter(object source)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                Witness? w = slots[i];
                if (w != null && w.Letter && ReferenceEquals(w.Source, source)) { w.Live = false; Expire(i, w); }
            }
        }
        internal void RefreshMessages(object[] live, int count)
        {
            if (count < 0 || count > 12 || count > live.Length) throw new ArgumentException();
            var members = new System.Collections.Generic.HashSet<object>(new Identity());
            for (int j = 0; j < count; j++) members.Add(live[j]);
            for (int i = 0; i < slots.Length; i++)
            {
                Witness? w = slots[i];
                if (w == null) continue;
                if (!ring.Contains(w.Event)) { slots[i] = null; continue; }
                if (w.Letter) continue;
                w.Live = members.Contains(w.Source);
                Expire(i, w);
            }
        }
        private void Expire(int index, Witness w)
        {
            if (w.Live || w.Archive != null) return;
            ring.Remove(w.Event); slots[index] = null;
        }
    }
}
