using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;

namespace RimPilot.Mod.Observation
{
    // Uniform outcome: hidden, absent, unaudited and malformed targets are all Unavailable.
    // Deferred = shared budget yielded/failed before a decision; never treated as ineligible.
    internal enum TargetGateResult { Eligible, Unavailable, Deferred }

    // Trusted game adapter view of an initialized fog source. Length is -1 when the backing
    // grid is not created. Fogged is only called for an index already proven < Length.
    internal interface IFogCells
    {
        int Length { get; }
        bool Fogged(int index);
    }

    // Trusted game adapter view of a pawn's hediff list, read from plain fields only (never
    // PsychologicallyVisible/ForcedVisible getters, which read ticks, holders and write a cache).
    internal interface IPawnConcealment
    {
        // -1 when the health tracker, hediff set or list is absent.
        int Hediffs { get; }
        // -1 for a null hediff; 0 for a hediff without components.
        int Comps(int hediff);
        // Invisibility component (any subclass) not definition-marked visible to the player.
        // Missing or foreign properties count as able to hide.
        bool MayHide(int hediff, int comp);
    }

    internal static class TargetGate
    {
        // Larger hediff/component lists need their own audit; never a partial scan.
        internal const int MaxHediffs = 256, MaxHediffComps = 32;

        // Larger footprints need their own audit; never a partial or approximate fog check.
        internal const int MaxFootprintCells = 256;

        // Map gate on primitive witnesses only. Size, cell-index geometry and fog grid must agree
        // exactly; a not-yet-created/disposed/resized fog source is not evidence of visibility.
        internal static TargetGateResult Map(int sizeX, int sizeZ, int indexSizeX, int indexSizeZ, int fogLength,
            bool disposed, bool generating)
        {
            if (disposed || generating || sizeX <= 0 || sizeZ <= 0 || sizeX != indexSizeX || sizeZ != indexSizeZ
                || (long)sizeX * sizeZ > int.MaxValue || fogLength != sizeX * sizeZ) return TargetGateResult.Unavailable;
            return TargetGateResult.Eligible;
        }

        // Installed Thing.SpawnSetup adds direct things to Map.spawnedThings. A non-null
        // holdingOwner is therefore required, but only that exact tracked map container.
        // Never traverse ParentHolder or accept another container merely owned by a map.
        internal static bool DirectSpawnHolder(object? mapContainer, object? holdingOwner) =>
            mapContainer != null && ReferenceEquals(mapContainer, holdingOwner);

        // Cells the caller must charge as nested work BEFORE Footprint; -1 when unsupported.
        internal static int FootprintCells(int width, int height)
        {
            if (width < 1 || height < 1 || width > MaxFootprintCells || height > MaxFootprintCells) return -1;
            int cells = width * height;
            return cells > MaxFootprintCells ? -1 : cells;
        }

        // Normal map-click fog semantics on the directly spawned target's occupied rectangle:
        // a one-cell target needs its in-bounds cell unfogged; a multi-cell target needs ANY
        // in-bounds occupied cell unfogged. Out-of-bounds cells never count as unfogged, unlike
        // the installed IsFogged(c) fallback. Reveals the target only, never other cell contents.
        internal static TargetGateResult Footprint<TFog>(TFog fog, int sizeX, int sizeZ,
            int minX, int minZ, int width, int height) where TFog : IFogCells
        {
            if (FootprintCells(width, height) < 0 || sizeX <= 0 || sizeZ <= 0
                || (long)sizeX * sizeZ != fog.Length) return TargetGateResult.Unavailable;
            for (int dz = 0; dz < height; dz++)
                for (int dx = 0; dx < width; dx++)
                {
                    long x = (long)minX + dx, z = (long)minZ + dz;
                    if (x < 0 || z < 0 || x >= sizeX || z >= sizeZ) continue;
                    int index = (int)(z * sizeX + x);
                    if (!fog.Fogged(index)) return TargetGateResult.Eligible;
                }
            return TargetGateResult.Unavailable;
        }

        // Normal hidden-from-player semantics (InvisibilityUtility.IsHiddenFromPlayer) without its
        // debug override: own-faction pawns stay inspectable even when invisible. Other pawns are
        // eligible only when no hediff carries an invisibility component that may hide them.
        // Whether such a pawn is currently revealed depends on ticks/holders/stuns, so a
        // concealment-capable non-player pawn is conservatively unavailable even while revealed.
        // Hediffs and components are charged as nested work before they are read.
        internal static TargetGateResult Pawn<TSource, TWork>(bool playerFactionKnown, bool ownFaction,
            TSource source, TWork work) where TSource : IPawnConcealment where TWork : INestedWork
        {
            if (!playerFactionKnown) return TargetGateResult.Unavailable;
            if (ownFaction) return TargetGateResult.Eligible;
            int hediffs = source.Hediffs;
            if (hediffs < 0 || hediffs > MaxHediffs) return TargetGateResult.Unavailable;
            if (hediffs == 0) return TargetGateResult.Eligible;
            if (!work.TryNestedFields(hediffs)) return TargetGateResult.Deferred;
            for (int h = 0; h < hediffs; h++)
            {
                int comps = source.Comps(h);
                if (comps < 0 || comps > MaxHediffComps) return TargetGateResult.Unavailable;
                if (comps == 0) continue;
                if (!work.TryNestedFields(comps)) return TargetGateResult.Deferred;
                for (int c = 0; c < comps; c++)
                    if (source.MayHide(h, c)) return TargetGateResult.Unavailable;
            }
            return TargetGateResult.Eligible;
        }
    }

    // Exact runtime types only. Inheritance, assembly membership or a familiar name never
    // extends admission; a subclass needs its own audit.
    internal sealed class TypeAdmission
    {
        internal const int MaxTypes = 32;
        private readonly Type[] types;
        internal TypeAdmission(params Type[] types)
        {
            if (types == null || types.Length == 0 || types.Length > MaxTypes) throw new ArgumentException("Invalid type admission.");
            foreach (Type type in types)
                if (type == null || type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
                    throw new ArgumentException("Invalid type admission.");
            this.types = (Type[])types.Clone();
        }
        internal bool Admits(object? target)
        {
            if (target == null) return false;
            Type actual = target.GetType();
            foreach (Type type in types) if (actual == type) return true;
            return false;
        }
    }

    // Runtime identity only: never invokes overloaded game Equals/GetHashCode.
    internal sealed class IdentityComparer : IEqualityComparer<object>
    {
        internal static readonly IdentityComparer Instance = new IdentityComparer();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }

    // Bounded eligible-only entity references for one root/game/map generation set. Callers
    // allocate only AFTER every target gate passed, and must re-run those gates on resolution.
    // Unknown, evicted, wrong-kind and wrong-map lookups are indistinguishable (null).
    internal sealed class ReferenceTable
    {
        private readonly object?[] targets, owners;
        private readonly string?[] ids;
        private readonly Dictionary<string, int> byId;
        private readonly Dictionary<object, int> byTarget;
        private int next, count;
        internal int Count => count;
        internal int Capacity => targets.Length;

        internal ReferenceTable(int capacity = WireLimits.TrackedEntities)
        {
            if (capacity < 1 || capacity > WireLimits.TrackedEntities) throw new ArgumentOutOfRangeException(nameof(capacity));
            targets = new object?[capacity]; owners = new object?[capacity]; ids = new string?[capacity];
            byId = new Dictionary<string, int>(capacity, StringComparer.Ordinal);
            byTarget = new Dictionary<object, int>(capacity, IdentityComparer.Instance);
        }

        // Stable while retained. When full, the oldest entry is evicted (it then resolves as
        // unavailable); eviction order depends only on allocation order, never on hidden state.
        internal string Allocate(string kind, object target, object owner)
        {
            if ((kind != "p" && kind != "t") || target == null || owner == null) throw new ArgumentException("Invalid reference.");
            if (byTarget.TryGetValue(target, out int existing))
            {
                if (ReferenceEquals(owners[existing], owner) && ids[existing]!.StartsWith(kind + "_", StringComparison.Ordinal))
                    return ids[existing]!;
                Remove(existing); // Moved map or kind: never alias the old reference.
            }
            int slot = next; next = (next + 1) % targets.Length;
            if (targets[slot] != null) Remove(slot);
            string id = Ids.New(kind);
            targets[slot] = target; owners[slot] = owner; ids[slot] = id; count++;
            byId.Add(id, slot); byTarget.Add(target, slot);
            return id;
        }

        internal object? Resolve(string kind, string id, object owner)
        {
            if (id == null || owner == null || !Ids.Is(id, kind) || !byId.TryGetValue(id, out int slot)
                || !ReferenceEquals(owners[slot], owner)) return null;
            return targets[slot];
        }

        internal void Clear()
        {
            Array.Clear(targets, 0, targets.Length); Array.Clear(owners, 0, owners.Length); Array.Clear(ids, 0, ids.Length);
            byId.Clear(); byTarget.Clear(); next = 0; count = 0;
        }

        private void Remove(int slot)
        {
            byId.Remove(ids[slot]!); byTarget.Remove(targets[slot]!);
            targets[slot] = null; owners[slot] = null; ids[slot] = null; count--;
        }
    }
}
