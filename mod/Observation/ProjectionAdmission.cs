using System;
using System.Reflection;

namespace RimPilot.Mod.Observation
{
    // Private policy families, not a target-dependent diagnostic or a wire capability list.
    internal enum ObservationFamily
    {
        SessionSummary, MapSummary, OwnPawnIdentity, OwnPawnStatus, ShownNeeds,
        VisibleHealth, EquippedGear, WornGear, CurrentJob, LordReport,
        MapThings, Resources, Selection, KnownWindows, PublicNotifications, RenderedControls
    }
    internal enum FamilyAvailability { Unverified, Unsupported }
    internal enum ProjectionResult { Value, Excluded, Unverified, BudgetExceeded }

    internal static class ObservationPolicy
    {
        // Policy-level status, independent of producers: a family stays Unverified until its
        // producer has real normal-UI proof (shown needs has a producer, not that proof yet).
        internal static FamilyAvailability Availability(ObservationFamily family) =>
            family == ObservationFamily.RenderedControls ? FamilyAvailability.Unsupported : FamilyAvailability.Unverified;
        internal static bool RequiredBaseline(ObservationFamily family) =>
            Enum.IsDefined(typeof(ObservationFamily), family) && family != ObservationFamily.RenderedControls;
    }

    // Local sub-budget only. Producer must supply one shared session/update budget and check
    // cancellation/epochs independently; creating a new instance per candidate is not permitted.
    internal sealed class ProjectionBudget
    {
        internal const int MaxOperations = 128, MaxBytes = 16384;
        private readonly Func<bool> available;
        private int operations, bytes;
        private bool exhausted;
        internal ProjectionBudget(Func<bool> available, int operations = MaxOperations, int bytes = MaxBytes)
        {
            if (available == null || operations < 0 || operations > MaxOperations || bytes < 0 || bytes > MaxBytes)
                throw new ArgumentOutOfRangeException();
            this.available = available; this.operations = operations; this.bytes = bytes;
        }
        internal bool Charge(int work = 1, int utf8Bytes = 0)
        {
            if (exhausted || work < 0 || utf8Bytes < 0 || work > operations || utf8Bytes > bytes || !available())
            { exhausted = true; operations = 0; bytes = 0; return false; }
            operations -= work; bytes -= utf8Bytes; return true;
        }
    }

    // Only trusted adapters supply these from bounded direct fields, never arbitrary properties.
    // Definition identity is reference identity: do not call Def.Equals/GetHashCode/ToString.
    internal sealed class ComponentAdmission
    {
        internal readonly Type RuntimeType;
        internal readonly object Definition;
        internal ComponentAdmission(Type runtimeType, object definition)
        { RuntimeType = runtimeType; Definition = definition; }
    }
    internal sealed class ComponentEvidence
    {
        internal readonly object Component, Definition;
        internal ComponentEvidence(object component, object definition)
        { Component = component; Definition = definition; }
    }

    internal interface IProjectionPatchAudit
    {
        // Includes EVERY installed owner and prefix/postfix/transpiler/finalizer/inner category.
        // False includes missing/failed evidence. Never execute patch methods/factories to audit.
        bool IsUnpatched(MethodBase method);
    }

    internal static class PatchCategories
    {
        internal static bool Unpatched(int prefixes, int postfixes, int transpilers, int finalizers,
            int innerPrefixes, int innerPostfixes) => prefixes == 0 && postfixes == 0 && transpilers == 0
            && finalizers == 0 && innerPrefixes == 0 && innerPostfixes == 0;
    }

    internal sealed class AdmissionRecipe
    {
        internal const int MaxComponents = 16, MaxMethods = 32;
        internal readonly ObservationFamily Family;
        internal readonly string Audit;
        private readonly Type runtimeType, definitionType;
        private readonly object definition;
        private readonly ComponentAdmission[] components;
        private readonly MethodBase[] closure;

        internal AdmissionRecipe(ObservationFamily family, string audit, Type runtimeType,
            object definition, Type definitionType, ComponentAdmission[] components, MethodBase[] closure)
        {
            if (!Enum.IsDefined(typeof(ObservationFamily), family) || string.IsNullOrEmpty(audit)
                || runtimeType == null || runtimeType.IsAbstract || runtimeType.ContainsGenericParameters
                || definition == null || definitionType == null || definition.GetType() != definitionType
                || components == null || components.Length > MaxComponents
                || closure == null || closure.Length == 0 || closure.Length > MaxMethods)
                throw new ArgumentException("Incomplete projection audit.");
            this.Family = family; Audit = audit; this.runtimeType = runtimeType;
            this.definition = definition; this.definitionType = definitionType;
            this.components = (ComponentAdmission[])components.Clone();
            this.closure = (MethodBase[])closure.Clone();
            foreach (ComponentAdmission component in this.components)
                if (component == null || component.RuntimeType == null || component.Definition == null)
                    throw new ArgumentException("Incomplete component audit.");
            foreach (MethodBase method in this.closure)
                if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.DeclaringType == null)
                    throw new ArgumentException("Incomplete provider audit.");
        }

        internal ProjectionResult Check(object candidate, object candidateDefinition,
            ComponentEvidence[] candidateComponents, IProjectionPatchAudit patches, ProjectionBudget budget)
        {
            if (!budget.Charge()) return ProjectionResult.BudgetExceeded;
            if (candidate == null || candidate.GetType() != runtimeType || candidateDefinition == null
                || candidateDefinition.GetType() != definitionType || !ReferenceEquals(candidateDefinition, definition)
                || candidateComponents == null || candidateComponents.Length != components.Length)
                return ProjectionResult.Unverified;
            // Exact order/cardinality is intentional: duplicates, extra comps, replacement props
            // and derived implementations all need a new audit, even if they share a label.
            for (int i = 0; i < components.Length; i++)
            {
                if (!budget.Charge()) return ProjectionResult.BudgetExceeded;
                ComponentEvidence actual = candidateComponents[i];
                ComponentAdmission expected = components[i];
                if (actual == null || actual.Component == null || actual.Component.GetType() != expected.RuntimeType
                    || !ReferenceEquals(actual.Definition, expected.Definition)) return ProjectionResult.Unverified;
            }
            // Empty patch allowlist: no stock/full-profile owner is trusted merely by its name.
            // Recipes fix provider/getter/report implementations; callers cannot substitute one.
            try
            {
                foreach (MethodBase method in closure)
                {
                    if (!budget.Charge()) return ProjectionResult.BudgetExceeded;
                    if (patches == null || !patches.IsUnpatched(method)) return ProjectionResult.Unverified;
                    if (!budget.Charge(0)) return ProjectionResult.BudgetExceeded;
                }
            }
            catch { return ProjectionResult.Unverified; }
            return ProjectionResult.Value;
        }
    }
}
