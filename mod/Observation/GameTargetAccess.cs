using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimPilot.Mod.Bridge;
using RimWorld;
using Unity.Collections;
using Verse;

namespace RimPilot.Mod.Observation
{
    // Installed-source audited map/target gates (docs/visibility-audit.md#target-gates). Game
    // thread only, after the dispatcher's readiness/developer/transition checks. Reads only
    // plain fields and field-returning getters in the patch-checked closure below: never
    // Thing.Spawned (logs on bad index), SpawnedParentOrMe/ParentHolder (holder traversal),
    // IsFogged(c) (false out of bounds/uninitialized) or selection/inspect helpers.
    internal sealed class GameTargetAccess
    {
        private readonly int gameThread;
        private readonly IProjectionPatchAudit patches;
        private readonly AccessTools.FieldRef<FogGrid, NativeBitArray> fogBits;
        private readonly AccessTools.FieldRef<Thing, sbyte> mapIndex;
        private readonly AccessTools.FieldRef<MapPawns, List<Pawn>> spawnedPawns;
        private readonly AccessTools.FieldRef<Pawn_GuestTracker, Faction> hostFaction;
        private readonly MethodBase[] closure;
        private int checkedFrame = -1;

        private struct GameFog : IFogCells
        {
            internal NativeBitArray Bits;
            public int Length => Bits.IsCreated ? Bits.Length : -1;
            public bool Fogged(int index) => Bits.IsSet(index);
        }

        // Plain-field hediff view; type tests only, no hediff/comp virtuals or getters.
        private struct GameConcealment : IPawnConcealment
        {
            internal List<Hediff>? List;
            public int Hediffs => List == null ? -1 : List.Count;
            public int Comps(int hediff)
            {
                Hediff value = List![hediff];
                if (value == null) return -1;
                return value is HediffWithComps withComps ? withComps.comps?.Count ?? 0 : 0;
            }
            public bool MayHide(int hediff, int comp) =>
                ((HediffWithComps)List![hediff]).comps[comp] is HediffComp_Invisibility invisibility
                && !(invisibility.props is HediffCompProperties_Invisibility props && props.visibleToPlayer);
        }

        internal GameTargetAccess(int gameThread, IProjectionPatchAudit patches)
        {
            if (Thread.CurrentThread.ManagedThreadId != gameThread) throw new InvalidOperationException("Game thread required.");
            this.gameThread = gameThread; this.patches = patches;
            fogBits = AccessTools.FieldRefAccess<FogGrid, NativeBitArray>(Field(typeof(FogGrid), "fogGrid", typeof(NativeBitArray)));
            mapIndex = AccessTools.FieldRefAccess<Thing, sbyte>(Field(typeof(Thing), "mapIndexOrState", typeof(sbyte)));
            spawnedPawns = AccessTools.FieldRefAccess<MapPawns, List<Pawn>>(Field(typeof(MapPawns), "pawnsSpawned", typeof(List<Pawn>)));
            hostFaction = AccessTools.FieldRefAccess<Pawn_GuestTracker, Faction>(Field(typeof(Pawn_GuestTracker), "hostFactionInt", typeof(Faction)));
            closure = new MethodBase[]
            {
                Getter(typeof(Map), "Disposed"), Getter(typeof(MapInfo), "Size"), Getter(typeof(Game), "Maps"),
                Getter(typeof(Game), "World"), Getter(typeof(FactionManager), "OfPlayer"),
                Getter(typeof(Thing), "Position"), Getter(typeof(Thing), "Rotation"), Getter(typeof(Thing), "Faction"),
                Getter(typeof(CellIndices), "SizeX"), Getter(typeof(CellIndices), "SizeZ"),
                Getter(typeof(NativeBitArray), "IsCreated"), Getter(typeof(NativeBitArray), "Length"),
                Method(typeof(NativeBitArray), "IsSet", typeof(int)),
                Method(typeof(GenAdj), "OccupiedRect", typeof(IntVec3), typeof(Rot4), typeof(IntVec2)),
                Method(typeof(GenAdj), "AdjustForRotation", typeof(IntVec3).MakeByRefType(), typeof(IntVec2).MakeByRefType(), typeof(Rot4)),
                Method(typeof(GenAdj), "AdjustForRotation", typeof(IntVec3).MakeByRefType(), typeof(IntVec2).MakeByRefType(),
                    typeof(Rot4), typeof(Rot4)),
                Getter(typeof(ListerThings), "AllThings"),
                Getter(typeof(Pawn_GuestTracker), "HostFaction"), Getter(typeof(Pawn_GuestTracker), "GuestStatus"),
                Getter(typeof(Pawn_GuestTracker), "IsSlave"), Getter(typeof(Pawn_GuestTracker), "IsPrisoner"), Getter(typeof(Rot4), "IsHorizontal"), Method(typeof(Rot4), "op_Inequality", typeof(Rot4), typeof(Rot4)),
                // Not called: a patch here means concealment/selection differs from the audit.
                Method(typeof(InvisibilityUtility), "IsHiddenFromPlayer", typeof(Pawn)),
                Method(typeof(ThingSelectionUtility), "SelectableByMapClick", typeof(Thing))
            };
        }

        // Normal player map access: every map in the current Game.Maps list is reachable through
        // the colonist bar's per-map entries (including empty maps), independent of the human's
        // current map, home status or pocket-map kind. The tracker supplies list membership; this
        // proves initialized, non-disposed geometry and a created fog grid of exactly that size.
        internal bool MapAccessible(Map map)
        {
            if (!Fresh() || map == null || map.info == null || map.fogGrid == null) return false;
            IntVec3 size = map.info.Size;
            var fog = new GameFog { Bits = fogBits(map.fogGrid) };
            return TargetGate.Map(size.x, size.z, map.cellIndices.SizeX, map.cellIndices.SizeZ, fog.Length,
                map.Disposed, MapGenerator.mapBeingGenerated != null) == TargetGateResult.Eligible;
        }

        // Raw list sources for bounded traversal; membership alone never implies eligibility.
        // Spawned pawns via the private backing list, never the shared helper result lists.
        internal List<Pawn>? SpawnedPawns(Map map) => Fresh() && map?.mapPawns != null ? spawnedPawns(map.mapPawns) : null;
        internal List<Thing>? AllThings(Map map) => Fresh() && map?.listerThings != null ? map.listerThings.AllThings : null;

        // Directly spawned exact-type target on an accessible tracked map. Charges footprint cells
        // as nested work before reading fog. Deferred means budget yielded/failed (see
        // work.Failure): never treat it as ineligible or emit a partial result.
        internal TargetGateResult Thing(Thing thing, Map map, Game game, TypeAdmission types, INestedWork work)
        {
            if (thing == null || map == null || game == null || types == null || work == null
                || !types.Admits(thing) || !MapAccessible(map)) return TargetGateResult.Unavailable;
            if (!DirectlySpawned(thing, map, game)) return TargetGateResult.Unavailable;
            ThingDef def = thing.def;
            if (def == null || !def.selectable) return TargetGateResult.Unavailable;
            if (thing is Pawn pawn)
            {
                Faction? player = game.World?.factionManager?.OfPlayer;
                TargetGateResult concealment = TargetGate.Pawn(player != null,
                    player != null && ReferenceEquals(thing.Faction, player),
                    new GameConcealment { List = pawn.health?.hediffSet?.hediffs }, work);
                if (concealment != TargetGateResult.Eligible) return concealment;
            }
            return Footprint(thing, map, work);
        }

        // Only the already-admitted own-pawn CleanFilth report calls this path. Nonselectable
        // filth has public short definition text in that report, not generic inspect permission.
        // No entity ref, sources, thickness or Filth.Label is exposed. Other targets still need
        // generic selectability; both paths retain identical direct-spawn/map/fog checks.
        internal TargetGateResult ReportFilth(Thing thing, Map map, Game game, INestedWork work)
        {
            if (thing == null || thing.GetType() != typeof(Filth) || map == null || game == null || work == null
                || !MapAccessible(map)) return TargetGateResult.Unavailable;
            ThingDef def = thing.def;
            if (def == null || def.GetType() != typeof(ThingDef) || def.thingClass != typeof(Filth)
                || def.category != ThingCategory.Filth || def.useHitPoints) return TargetGateResult.Unavailable;
            return DirectFootprint(thing, map, game, work);
        }

        private TargetGateResult DirectFootprint(Thing thing, Map map, Game game, INestedWork work) =>
            DirectlySpawned(thing, map, game) ? Footprint(thing, map, work) : TargetGateResult.Unavailable;

        private bool DirectlySpawned(Thing thing, Map map, Game game)
        {
            List<Map> maps = game.Maps;
            sbyte index = mapIndex(thing);
            // Installed spawn setup inserts direct things into this exact map container.
            // Other containers (carried/contained/another map) and stale indices refuse.
            if (maps == null || index < 0 || index >= maps.Count || !ReferenceEquals(maps[index], map)
                || !TargetGate.DirectSpawnHolder(map.spawnedThings, thing.holdingOwner)) return false;
            return true;
        }

        private TargetGateResult Footprint(Thing thing, Map map, INestedWork work)
        {
            ThingDef def = thing.def;
            CellRect rect = GenAdj.OccupiedRect(thing.Position, thing.Rotation, def.size);
            long width = (long)rect.maxX - rect.minX + 1, height = (long)rect.maxZ - rect.minZ + 1;
            int cells = width > int.MaxValue || height > int.MaxValue ? -1 : TargetGate.FootprintCells((int)width, (int)height);
            if (cells < 0) return TargetGateResult.Unavailable;
            if (!work.TryNestedFields(cells)) return TargetGateResult.Deferred;
            IntVec3 size = map.info.Size;
            return TargetGate.Footprint(new GameFog { Bits = fogBits(map.fogGrid) }, size.x, size.z,
                rect.minX, rect.minZ, (int)width, (int)height);
        }

        // Own colony pawn (docs/visibility-audit.md#own-pawns): player faction and no guest or
        // prisoner host; colony slaves stay own. Plain fields only (guest host/status).
        internal bool OwnPawn(Pawn pawn, Faction? player)
        {
            if (!Fresh() || pawn == null || player == null || !ReferenceEquals(pawn.Faction, player)) return false;
            Pawn_GuestTracker? guest = pawn.guest;
            Faction? host = guest == null ? null : hostFaction(guest);
            return PawnProjection.Own(true, host != null,
                host != null && ReferenceEquals(host, player) && guest!.guestStatusInt == GuestStatus.Slave);
        }

        // Plain-field inputs of Thing() and OwnPawn(): spawn index, holder, definition, position,
        // rotation, faction and pawn guest host/status, plus the hediff list identity/version for
        // pawns outside the player faction. Fog is covered separately by EditCounter.Fog.
        // Captured with every gate decision.
        internal struct Witness
        {
            internal sbyte MapIndex;
            internal int StackCount;
            internal GuestStatus GuestStatus;
            internal object? Holder, MapHolder, Def, Faction, Hediffs, Guest, Host;
            internal Map? Map;
            internal IntVec3 Position;
            internal Rot4 Rotation;
            internal bool Concealment;
            internal List<Hediff>.Enumerator Version;
        }

        internal Witness Capture(Thing thing, Faction? player, Map map)
        {
            var witness = new Witness
            {
                MapIndex = mapIndex(thing), StackCount = thing.stackCount, Holder = thing.holdingOwner,
                Map = map, MapHolder = map.spawnedThings, Def = thing.def,
                Position = thing.Position, Rotation = thing.Rotation, Faction = thing.Faction
            };
            if (thing is Pawn guestPawn && guestPawn.guest != null)
            {
                witness.Guest = guestPawn.guest; witness.Host = hostFaction(guestPawn.guest);
                witness.GuestStatus = guestPawn.guest.guestStatusInt;
            }
            if (thing is Pawn pawn && (player == null || !ReferenceEquals(witness.Faction, player)))
            {
                List<Hediff>? hediffs = pawn.health?.hediffSet?.hediffs;
                witness.Concealment = true; witness.Hediffs = hediffs;
                if (hediffs != null) witness.Version = hediffs.GetEnumerator();
            }
            return witness;
        }

        // Plain field comparison only; the patch closure must still match this frame.
        internal bool Unchanged(Thing thing, Witness witness)
        {
            if (!Fresh() || mapIndex(thing) != witness.MapIndex || thing.stackCount != witness.StackCount
                || !ReferenceEquals(thing.holdingOwner, witness.Holder)
                || witness.Map == null || !ReferenceEquals(witness.Map.spawnedThings, witness.MapHolder)
                || !ReferenceEquals(thing.def, witness.Def) || !Same(thing.Position, witness.Position)
                || thing.Rotation != witness.Rotation || !ReferenceEquals(thing.Faction, witness.Faction)) return false;
            if (thing is Pawn guestPawn)
            {
                Pawn_GuestTracker? guest = guestPawn.guest;
                if (!ReferenceEquals(guest, witness.Guest)
                    || (guest != null && (!ReferenceEquals(hostFaction(guest), witness.Host) || guest.guestStatusInt != witness.GuestStatus)))
                    return false;
            }
            if (!witness.Concealment) return true;
            List<Hediff>? hediffs = (thing as Pawn)?.health?.hediffSet?.hediffs;
            if (!ReferenceEquals(hediffs, witness.Hediffs)) return false;
            if (hediffs == null) return true;
            List<Hediff>.Enumerator version = witness.Version;
            try { version.MoveNext(); }
            catch (InvalidOperationException) { return false; }
            return true;
        }

        private static bool Same(IntVec3 a, IntVec3 b) => a.x == b.x && a.y == b.y && a.z == b.z;

        // Whole closure rechecked once per rendered frame; any patch owner/category fails closed.
        private bool Fresh()
        {
            if (Thread.CurrentThread.ManagedThreadId != gameThread) throw new InvalidOperationException("Game thread required.");
            int frame = UnityEngine.Time.frameCount;
            if (frame == checkedFrame) return true;
            try
            {
                foreach (MethodBase method in closure)
                    if (!patches.IsUnpatched(method)) return false;
            }
            catch { return false; }
            checkedFrame = frame;
            return true;
        }

        private static FieldInfo Field(Type type, string name, Type fieldType)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field == null || field.FieldType != fieldType) throw new InvalidOperationException("Target binding unavailable.");
            return field;
        }
        private static MethodInfo Getter(Type type, string property) =>
            type.GetProperty(property, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                ?.GetGetMethod() ?? throw new InvalidOperationException("Target binding unavailable.");
        private static MethodInfo Method(Type type, string name, params Type[] parameters) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly, null, parameters, null)
                ?? throw new InvalidOperationException("Target binding unavailable.");
    }
}
