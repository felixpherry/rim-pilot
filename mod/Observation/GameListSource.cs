using System;
using System.Collections.Generic;
using RimPilot.Mod.Bridge;
using RimWorld;
using Verse;

namespace RimPilot.Mod.Observation
{
    // Audited structure and eligibility witnesses for one map list (docs/visibility-audit.md#list-witnesses).
    // Game thread only. Created when traversal starts; structure is the raw list's version,
    // eligibility the fog edit counter plus each member's gate-input witness.
    internal sealed class GameListSource<T> : ISnapshotSource where T : Thing
    {
        private readonly GameTargetAccess access;
        private readonly Map map;
        private readonly Game game;
        private readonly TypeAdmission types;
        private readonly Func<Map, List<T>?> read;
        private readonly ListWitness<T> structure;
        private readonly MemberWitnesses<GameTargetAccess.Witness> eligibility;
        internal Faction? Player { get; }
        internal List<T> List => structure.List;

        internal GameListSource(GameTargetAccess access, Map map, Game game, TypeAdmission types, Func<Map, List<T>?> read)
        {
            if (access == null || map == null || game == null || types == null || read == null)
                throw new ArgumentException("Invalid list source.");
            this.access = access; this.map = map; this.game = game; this.types = types; this.read = read;
            structure = new ListWitness<T>(read(map) ?? throw new InvalidOperationException("List source unavailable."));
            Player = game.World?.factionManager?.OfPlayer;
            eligibility = new MemberWitnesses<GameTargetAccess.Witness>(EditCounter.Fog,
                (member, state) => access.Unchanged((Thing)member, state));
        }

        // Version 0 while the build-start list is untouched; any mutation latches 1.
        public bool TryStructure(out long version)
        {
            version = structure.Unchanged(read(map)) ? 0 : 1;
            return true;
        }
        public bool TryEligibility(out long revision) => eligibility.TryRevision(out revision);
        public TargetGateResult Revalidate(object member, INestedWork work) =>
            member is T thing ? Gate(thing, work, out _) : TargetGateResult.Unavailable;

        // Records inputs for a member the caller excludes without gating (e.g. not own faction):
        // a later change to them must still invalidate the caller's aggregate.
        internal bool Witness(T thing) => thing != null && eligibility.Record(thing, access.Capture(thing, Player, map));

        // Every decided gate (eligible or not) records its inputs. A full witness table cannot
        // protect the decision: witnessed=false and the caller must fail/omit, never publish.
        internal TargetGateResult Gate(T thing, INestedWork work, out bool witnessed)
        {
            GameTargetAccess.Witness inputs = access.Capture(thing, Player, map);
            TargetGateResult result = access.Thing(thing, map, game, types, work);
            witnessed = result != TargetGateResult.Deferred && eligibility.Record(thing, inputs);
            return witnessed || result == TargetGateResult.Deferred ? result : TargetGateResult.Unavailable;
        }
    }
}
