using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;
using RimWorld;
using Verse;

namespace RimPilot.Mod.Observation
{
    // Observe canonical delivery only. Never call AcceptsMessage, CanShowInLetterStack,
    // Archive.Contains, timer/hover/selection methods or restored-history enumeration.
    internal sealed class GamePublicEvents : IObservationHistorySink
    {
        private const string Owner = "rim-pilot.observation.public-events.v1";
        private static GamePublicEvents? installed;
        private readonly PublicEventRing ring = new PublicEventRing(() => (long)(Monotonic.Now * (1000.0 / System.Diagnostics.Stopwatch.Frequency)));
        private readonly PublicEventWitnesses witnesses;
        private readonly AccessTools.FieldRef<List<Message>> live;
        private readonly AccessTools.FieldRef<Root, bool> destroyed;
        private readonly Func<UpdateBudget, Scope?> scope;
        private readonly int thread;
        private readonly MethodInfo[] hooked, getters;
        private int depth, calls, candidates, nested;
        private long version, spent;
        private Ticket? current;
        private bool enabled;
        internal long SpentTicks => spent;
        internal int Candidates => candidates;
        internal int NestedFields => nested;
        internal EventProducer<Map> Producer { get; }

        internal sealed class Ticket
        {
            internal object Source = null!, List = null!;
            internal int Count;
            internal long Version;
            internal bool Eligible, Finished, Archived;
            internal Archive? Archive;
            internal Scope? Scope;
        }

        internal GamePublicEvents(Func<UpdateBudget, Scope?> scope)
        {
            this.scope = scope; thread = Thread.CurrentThread.ManagedThreadId;
            witnesses = new PublicEventWitnesses(ring);
            FieldInfo field = typeof(Messages).GetField("liveMessages", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                ?? throw new InvalidOperationException();
            if (field.FieldType != typeof(List<Message>)) throw new InvalidOperationException();
            live = AccessTools.StaticFieldRefAccess<List<Message>>(field);
            FieldInfo rootField = typeof(Root).GetField("destroyed", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                ?? throw new InvalidOperationException();
            if (rootField.FieldType != typeof(bool)) throw new InvalidOperationException();
            destroyed = AccessTools.FieldRefAccess<Root, bool>(rootField);
            hooked = new[] {
                Exact(typeof(LetterStack), "ReceiveLetter", typeof(Letter), typeof(string), typeof(int), typeof(bool)),
                Exact(typeof(Messages), "Message", typeof(Message), typeof(bool)),
                Exact(typeof(Archive), "Add", typeof(IArchivable)), Exact(typeof(Archive), "Remove", typeof(IArchivable)),
                Exact(typeof(LetterStack), "RemoveLetter", typeof(Letter)),
                Exact(typeof(Messages), "Update"), Exact(typeof(Messages), "Clear")
            };
            getters = new[] {
                GameKeyedText.Getter(typeof(LetterStack), "LettersListForReading"),
                GameKeyedText.Getter(typeof(Letter), "Label"), GameKeyedText.Getter(typeof(ChoiceLetter), "Text"),
                GameKeyedText.Getter(typeof(TaggedString), "RawText"),
                GameKeyedText.Getter(typeof(Find), "Archive", true), GameKeyedText.Getter(typeof(Find), "History", true),
                GameKeyedText.Getter(typeof(Find), "LetterStack", true),
                GameKeyedText.Getter(typeof(Current), "Root", true), GameKeyedText.Getter(typeof(Current), "Game", true),
                GameKeyedText.Getter(typeof(Current), "ProgramState", true),
                GameKeyedText.Getter(typeof(LongEventHandler), "AnyEventNowOrWaiting", true),
                GameKeyedText.Getter(typeof(GenScene), "InEntryScene", true), GameKeyedText.Getter(typeof(GenScene), "InPlayScene", true)
            };
            Producer = new EventProducer<Map>(ring, Prepare);
        }
        internal void Install()
        {
            installed = this;
            var harmony = new Harmony(Owner);
            string[] before = { nameof(BeforeLetter), nameof(BeforeMessage), nameof(BeforeArchiveAdd), nameof(BeforeArchiveRemove), nameof(BeforeLetterRemove), nameof(BeforeMessagesChanged), nameof(BeforeMessagesChanged) };
            string[] after = { nameof(AfterLetter), nameof(AfterMessage), nameof(AfterArchiveAdd), nameof(AfterArchiveRemove), nameof(AfterLetterRemove), nameof(AfterMessagesChanged), nameof(AfterMessagesChanged) };
            for (int i = 0; i < hooked.Length; i++) harmony.Patch(hooked[i], prefix: Hook(before[i], Priority.First), finalizer: Hook(after[i], Priority.Last));
            enabled = true;
        }
        private static MethodInfo Exact(Type type, string name, params Type[] args) => type.GetMethod(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly,
            null, args, null) ?? throw new InvalidOperationException();
        private static HarmonyMethod Hook(string name, int priority) => new HarmonyMethod(typeof(GamePublicEvents).GetMethod(name,
            BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException()) { priority = priority };
        private bool Fresh()
        {
            var audit = new GameProjectionPatchAudit();
            foreach (MethodInfo getter in getters) if (!audit.IsUnpatched(getter)) return false;
            foreach (MethodInfo method in hooked)
            {
                Patches? p = Harmony.GetPatchInfo(method);
                if (p == null || p.Prefixes.Count != 1 || p.Finalizers.Count != 1 || p.Postfixes.Count != 0
                    || p.Transpilers.Count != 0 || p.InnerPrefixes.Count != 0 || p.InnerPostfixes.Count != 0
                    || p.Prefixes[0].owner != Owner || p.Finalizers[0].owner != Owner) return false;
            }
            return true;
        }
        // Reset once per ordinary Update, not per request, map, hook or GUI pass. Hook CPU
        // elapsed is subtracted from dispatch's same 2-ms session allowance. Originals are
        // excluded: their gameplay work is not bridge observation work.
        internal void BeginUpdate() { if (Thread.CurrentThread.ManagedThreadId == thread) { spent = 0; calls = 0; candidates = nested = 0; } }
        private bool Reserve(int scan, int fields)
        {
            if (scan < 0 || fields < 0 || scan > WireLimits.CandidatesPerUpdate - candidates
                || fields > WireLimits.NestedFieldsPerUpdate - nested)
            { Invalidate(ObservationInvalidation.All); return false; }
            candidates += scan; nested += fields; return true;
        }
        private void Run(Action<UpdateBudget> action)
        {
            if (!enabled) return;
            if (Thread.CurrentThread.ManagedThreadId != thread) { Invalidate(ObservationInvalidation.All); return; }
            long start = Monotonic.Now;
            try
            {
                var budget = new UpdateBudget(() => Monotonic.Now, spent);
                lock (ring.Sync)
                {
                    if (++calls > 64 || !budget.Available) { Invalidate(ObservationInvalidation.All); return; }
                    action(budget);
                    if (!budget.Available) Invalidate(ObservationInvalidation.All);
                }
            }
            catch { Invalidate(ObservationInvalidation.All); }
            finally { spent += Math.Max(0, Monotonic.Now - start); }
        }
        internal void Disable() { enabled = false; Invalidate(ObservationInvalidation.All); }
        public void Invalidate(ObservationInvalidation cause)
        {
            lock (ring.Sync)
            {
                if (version == long.MaxValue) enabled = false;
                else version++;
                ring.Suspend(); witnesses.Clear();
            }
        }
        private Scope? Safe(UpdateBudget budget)
        {
            if (!Fresh() || !budget.Available) { Invalidate(ObservationInvalidation.All); return null; }
            Root root = Current.Root;
            if (root == null || destroyed(root)) { Invalidate(ObservationInvalidation.All); return null; }
            Scope? value = scope(budget);
            if (value == null || !budget.Available) { Invalidate(ObservationInvalidation.All); return null; }
            ring.Ready(value); return value;
        }
        private bool Prepare(ProducerWork<Map> work)
        {
            // Availability is maintained by successful archive Add and all Remove calls,
            // never an unbounded archive scan (pinned history has no finite source cap).
            if (!enabled) { Invalidate(ObservationInvalidation.All); return work.Fail(WireError.Unavailable); }
            // A charge that yields (allowance spent) or already failed is not evidence about the
            // hooks: keep history and retry in the next update; a recorded failure still ends the read.
            if (!work.TryNestedFields(280)) return false;
            if (!Fresh()) { Invalidate(ObservationInvalidation.All); return work.Fail(WireError.Unavailable); }
            List<Message> messages = live();
            if (messages == null || messages.Count > 12) { Invalidate(ObservationInvalidation.All); return work.Fail(WireError.Unavailable); }
            ring.Ready(work.Scope); Refresh(messages);
            return true;
        }
        private void Refresh(List<Message> messages)
        {
            if (messages == null || messages.Count > 12) { Invalidate(ObservationInvalidation.All); return; }
            var refs = new object[12];
            for (int i = 0; i < messages.Count; i++) refs[i] = messages[i];
            witnesses.RefreshMessages(refs, messages.Count);
        }
        private Ticket Begin(object source, object list, int count, bool already, int delay, Scope acceptedScope)
        {
            depth++;
            var ticket = new Ticket { Source = source, List = list, Count = count, Version = version };
            // One canonical call in flight. Nested delivery is omitted, never queued/replayed.
            if (depth != 1 || already || delay > 0 || count < 0 || count > 256 || Scribe.mode != LoadSaveMode.Inactive) return ticket;
            ticket.Scope = acceptedScope; ticket.Version = version;
            ticket.Eligible = true;
            current = ticket;
            return ticket;
        }
        private void Finish(Ticket ticket, bool member, bool letter, Func<string?> label, Func<string?> text, UpdateBudget budget)
        {
            if (ticket.Finished) return;
            ticket.Finished = true; depth = Math.Max(0, depth - 1);
            if (ReferenceEquals(current, ticket)) current = null;
            if (!ticket.Eligible || !member || ticket.Version != version || !Reserve(0, 258)) return;
            Scope? now = Safe(budget);
            if (now == null || ticket.Scope == null || !ticket.Scope.Same(now) || ticket.Version != version) return;
            string? rawLabel = label();
            string? cleanLabel = rawLabel == null ? null : PublicEventText.Sanitize(rawLabel, 512);
            string? cleanText = PublicEventText.Sanitize(text(), 2048);
            if (cleanText == null || (letter && cleanLabel == null)) return;
            witnesses.Accepted(new object(), ticket.Source, ticket.Archived ? ticket.Archive : null, letter, cleanLabel, cleanText);
        }
        private static bool Present<T>(List<T> list, T source, int max) where T : class
        {
            if (list.Count > max) return true; // Refuse before any unbounded scan.
            for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], source)) return true;
            return false;
        }
        private static void BeforeLetter(LetterStack __instance, Letter let, int delayTicks, List<Letter> ___letters, out Ticket? __state)
        {
            Ticket? state = null;
            installed?.Run(b => {
                if (let == null || let.GetType() != typeof(StandardLetter) || delayTicks > 0) return;
                Scope? safe = installed.Safe(b); if (safe == null) return;
                if (!ReferenceEquals(__instance, Find.LetterStack) || !installed.Reserve(___letters.Count, 10)) return;
                state = installed.Begin(let, ___letters, ___letters.Count, Present(___letters, let, 128), delayTicks, safe);
            });
            __state = state;
        }
        private static void AfterLetter(Letter let, List<Letter> ___letters, ref Ticket? __state, Exception? __exception)
        {
            Ticket? state = __state; __state = null;
            if (state == null) return;
            installed?.Run(b => installed.Finish(state, __exception == null && state.Archived
                && ReferenceEquals(state.List, ___letters) && ___letters.Count == state.Count + 1
                && ReferenceEquals(___letters[___letters.Count - 1], let), true, () => let.Label.RawText, () => ((ChoiceLetter)let).Text.RawText, b));
            installed?.Balance(state);
        }
        private static void BeforeMessage(Message msg, List<Message> ___liveMessages, out Ticket? __state)
        {
            Ticket? state = null;
            installed?.Run(b => {
                if (msg == null || msg.GetType() != typeof(Message)) return;
                Scope? safe = installed.Safe(b); if (safe == null) return;
                if (!installed.Reserve(___liveMessages.Count, 10)) return;
                state = installed.Begin(msg, ___liveMessages, ___liveMessages.Count, Present(___liveMessages, msg, 12), 0, safe);
            });
            __state = state;
        }
        private static void AfterMessage(Message msg, List<Message> ___liveMessages, ref Ticket? __state, Exception? __exception)
        {
            Ticket? state = __state; __state = null;
            if (state == null) { installed?.Invalidate(ObservationInvalidation.All); return; }
            installed?.Run(b => {
                if (!installed.Reserve(0, 268)) return;
                installed.Refresh(___liveMessages);
                installed.Finish(state, __exception == null && ReferenceEquals(state.List, ___liveMessages)
                    && ___liveMessages.Count == Math.Min(12, state.Count + 1)
                    && ___liveMessages.Count > 0 && ReferenceEquals(___liveMessages[___liveMessages.Count - 1], msg), false, () => null, () => msg.text, b);
            });
            installed?.Balance(state);
        }
        private void Balance(Ticket ticket)
        {
            // Even an exhausted budget/throwing hook must balance reentrancy without touching
            // game state; finalizer retries see null __state and cannot decrement twice.
            if (!ticket.Finished) { ticket.Finished = true; depth = Math.Max(0, depth - 1); }
            if (ReferenceEquals(current, ticket)) current = null;
        }
        private static void BeforeMessagesChanged(out bool __state) { __state = true; }
        private static void AfterMessagesChanged(List<Message> ___liveMessages, ref bool __state, Exception? __exception)
        {
            bool entered = __state; __state = false;
            if (!entered || __exception != null) { installed?.Invalidate(ObservationInvalidation.All); return; }
            installed?.Run(_ => { if (installed.Reserve(0, 268)) installed.Refresh(___liveMessages); });
        }
        private static void BeforeArchiveAdd(Archive __instance, IArchivable archivable, out Ticket? __state)
        {
            // No scans or original calls. Record only the matching in-flight canonical object.
            __state = installed != null && Thread.CurrentThread.ManagedThreadId == installed.thread
                && installed.depth == 1 && ReferenceEquals(installed.current?.Source, archivable) ? installed.current : null;
        }
        private static void AfterArchiveAdd(Archive __instance, bool __result, ref Ticket? __state, Exception? __exception)
        {
            Ticket? state = __state; __state = null;
            if (state != null && __exception == null && __result) { state.Archive = __instance; state.Archived = true; }
        }
        private static void BeforeArchiveRemove(Archive __instance, IArchivable archivable, out bool __state)
        {
            __state = true;
            installed?.Run(_ => { if (installed.Reserve(0, 256)) installed.witnesses.RemovedFromArchive(__instance, archivable); });
        }
        private static void AfterArchiveRemove(ref bool __state, Exception? __exception)
        {
            if (!__state || __exception != null) installed?.Invalidate(ObservationInvalidation.All);
            __state = false;
        }
        private static void BeforeLetterRemove(Letter let, out bool __state)
        {
            __state = true;
            installed?.Run(_ => { if (installed.Reserve(0, 256)) installed.witnesses.RemovedLetter(let); });
        }
        private static void AfterLetterRemove(ref bool __state, Exception? __exception)
        {
            if (!__state || __exception != null) installed?.Invalidate(ObservationInvalidation.All);
            __state = false;
        }
    }
}
