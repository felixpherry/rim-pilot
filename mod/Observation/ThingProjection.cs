using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimPilot.Protocol;

namespace RimPilot.Mod.Observation
{
    internal static class ThingProjection
    {
        internal const string ResourceBasis = "eligible_observed_direct_spawned_stack_units";
        internal static JObject Item(string id, string label, int x, int z, int units)
        {
            if (!Ids.Is(id, "t") || JobProjection.Public(label) == null || x < 0 || x > 4095
                || z < 0 || z > 4095 || units < 1) throw new ArgumentException("Invalid thing.");
            return new JObject { ["thing"] = id, ["label"] = label, ["x"] = x, ["z"] = z, ["stackUnits"] = units };
        }

        // Keys are audited public definition identities, never runtime types or localized labels.
        // Preserve first-observed order and keep distinct definitions with identical labels separate.
        internal sealed class Resources
        {
            private readonly Dictionary<object, JObject> kinds = new Dictionary<object, JObject>(IdentityComparer.Instance);
            private readonly JArray rows = new JArray();
            internal int Count => kinds.Count;
            internal bool Add(object kind, string label, int units)
            {
                if (kind == null || JobProjection.Public(label) == null || units < 1) return false;
                if (kinds.TryGetValue(kind, out JObject? row))
                {
                    if ((string?)row["label"] != label || (long)row["stackUnits"]! > long.MaxValue - units) return false;
                    row["stackUnits"] = (long)row["stackUnits"]! + units;
                }
                else
                {
                    if (kinds.Count >= 256) return false;
                    row = new JObject { ["label"] = label, ["stackUnits"] = (long)units };
                    kinds.Add(kind, row); rows.Add(row);
                }
                return true;
            }
            internal JObject Result(SampleStamp sample) => new JObject
            { ["sample"] = sample.Json(), ["basis"] = ResourceBasis, ["items"] = rows.DeepClone() };
        }
    }

    // Unlike an ordinary historical list, an exact aggregate must preserve EVERY decision,
    // including excluded members. Never reset changed witnesses and then publish an old vector.
    internal sealed class ConsistentSnapshotSource : ISnapshotSource
    {
        private readonly ISnapshotSource source;
        private readonly long initial;
        private readonly Func<bool>? admitted;
        private bool changed;
        internal ConsistentSnapshotSource(ISnapshotSource source, Func<bool>? admitted = null)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.admitted = admitted;
            changed = !source.TryEligibility(out initial);
        }
        public bool TryStructure(out long version)
        {
            if ((admitted != null && !admitted()) || !source.TryStructure(out long structure) || structure != 0
                || !source.TryEligibility(out long revision) || revision != initial) changed = true;
            version = changed ? 1 : 0;
            return true;
        }
        public bool TryEligibility(out long revision) => source.TryEligibility(out revision);
        public TargetGateResult Revalidate(object member, RimPilot.Mod.Bridge.INestedWork work) => source.Revalidate(member, work);
    }
}
