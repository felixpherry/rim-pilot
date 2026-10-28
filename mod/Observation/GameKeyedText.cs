using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimPilot.Mod.Observation
{
    // Keyed text and capitalization mirrors shared by the detail family readers. Game thread
    // only. Never loads language data, creates a language worker, pseudo-translates, logs or
    // fills label caches; anything it cannot mirror exactly throws, and the caller omits its family.
    internal sealed class GameKeyedText
    {
        private readonly AccessTools.FieldRef<Def, TaggedString> cachedLabel = Field<Def, TaggedString>("cachedLabelCap");
        private readonly AccessTools.FieldRef<LoadedLanguage, bool> loaded = Field<LoadedLanguage, bool>("dataIsLoaded");
        private readonly AccessTools.FieldRef<LoadedLanguage, LanguageWorker> worker = Field<LoadedLanguage, LanguageWorker>("workerInt");

        // Patch-checked by every reader that formats or translates.
        internal static MethodBase[] Closure() => new MethodBase[]
        {
            Method(typeof(Translator), "Translate", typeof(string)), Method(typeof(Translator), "TryTranslate", typeof(string), typeof(TaggedString).MakeByRefType()),
            Method(typeof(LoadedLanguage), "TryGetTextFromKey", typeof(string), typeof(TaggedString).MakeByRefType()),
            Method(typeof(TranslatorFormattedStringExtensions), "Translate", typeof(string), typeof(NamedArgument)),
            Method(typeof(TranslatorFormattedStringExtensions), "Translate", typeof(string), typeof(NamedArgument), typeof(NamedArgument)),
            Method(typeof(GrammarResolverSimpleStringExtensions), "Formatted", typeof(TaggedString), typeof(NamedArgument)),
            Method(typeof(GrammarResolverSimpleStringExtensions), "Formatted", typeof(TaggedString), typeof(NamedArgument), typeof(NamedArgument)),
            Method(typeof(GrammarResolverSimpleStringExtensions), "Formatted", typeof(string), typeof(NamedArgument)),
            Method(typeof(GrammarResolverSimpleStringExtensions), "Formatted", typeof(string), typeof(NamedArgument), typeof(NamedArgument)),
            Method(typeof(GrammarResolverSimple), "Formatted", typeof(TaggedString), typeof(System.Collections.Generic.List<string>), typeof(System.Collections.Generic.List<object>)),
            Method(typeof(GenText), "CapitalizeSentences", typeof(string), typeof(bool)), Method(typeof(GenText), "MergeMultipleSpaces", typeof(string), typeof(bool)),
            Method(typeof(LanguageWorker), "PostProcessed", typeof(string)), Method(typeof(LanguageWorker_English), "PostProcessed", typeof(string)),
            Method(typeof(GenText), "CapitalizeFirst", typeof(string)), Method(typeof(GenText), "CapitalizeFirst", typeof(string), typeof(Def)),
            Method(typeof(GenText), "FirstLetterBetweenTags", typeof(string)), Method(typeof(ColoredText), "Resolve", typeof(TaggedString)),
            Getter(typeof(Def), "LabelCap"), Getter(typeof(LoadedLanguage), "Worker"), Getter(typeof(Find), "ActiveLanguageWorker", true),
            Getter(typeof(Prefs), "DevMode", true),
        };

        // GenText.CapitalizeFirst(str, def): def.LabelCap when str is the def label (cache must
        // agree), else CapitalizeFirst. Markup or empty text is refused.
        internal string Cap(string? text, Def? def)
        {
            string label = PawnProjection.Label("", text) ?? throw Refuse();
            if (def != null && text == def.label)
            {
                string cached = cachedLabel(def).RawText;
                if (!string.IsNullOrEmpty(cached) && !string.Equals(cached, label, StringComparison.Ordinal)) throw Refuse();
            }
            return label;
        }

        // Translator.Translate(key) for a plain keyed string, read without loading language data,
        // pseudo-translating or logging. Markup would be stripped by the implicit string
        // conversion, so it is refused instead.
        internal string Keyed(string key)
        {
            LoadedLanguage? active = LanguageDatabase.activeLanguage;
            if (active == null || !loaded(active)) throw Refuse();
            string? value;
            if (active.keyedReplacements.TryGetValue(key, out LoadedLanguage.KeyedReplacement? found) && found != null && !found.isPlaceholder)
                value = found.value;
            else
            {
                LoadedLanguage? fallback = LanguageDatabase.defaultLanguage;
                if (Prefs.DevMode || fallback == null || !loaded(fallback)) throw Refuse();
                value = fallback.keyedReplacements.TryGetValue(key, out found) && found != null && !found.isPlaceholder ? found.value : key;
            }
            if (string.IsNullOrEmpty(value) || value!.IndexOf('<') >= 0 || value.IndexOf("(*", StringComparison.Ordinal) >= 0) throw Refuse();
            return value;
        }

        // key.Translate(args) through the restricted formatter mirror; the active language's
        // worker must already exist (never created here) and be the base or English worker.
        internal string Format(string key, params FormatArg[] args)
        {
            return Formatted(Keyed(key), args);
        }

        internal string Formatted(string template, params FormatArg[] args)
        {
            LanguageWorker? current = LanguageDatabase.activeLanguage == null ? null : worker(LanguageDatabase.activeLanguage);
            if (current == null) throw Refuse();
            Type type = current.GetType();
            bool english = type == typeof(LanguageWorker_English);
            if (!english && (type.Assembly != typeof(LanguageWorker).Assembly
                || type.GetMethod("PostProcessed", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null)?.DeclaringType != typeof(LanguageWorker)))
                throw Refuse();
            return HealthProjection.Formatted(template, args, english) ?? throw Refuse();
        }

        private static Exception Refuse() => new InvalidOperationException("Keyed text unverified.");
        private static AccessTools.FieldRef<TOwner, TField> Field<TOwner, TField>(string name)
        {
            FieldInfo? field = typeof(TOwner).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field == null || field.FieldType != typeof(TField)) throw new InvalidOperationException("Text binding unavailable.");
            return AccessTools.FieldRefAccess<TOwner, TField>(field);
        }
        internal static MethodInfo Getter(Type type, string property, bool isStatic = false) =>
            type.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.DeclaredOnly)?.GetGetMethod(true)
                ?? throw new InvalidOperationException("Text binding unavailable.");
        internal static MethodInfo Method(Type type, string name, params Type[] parameters) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly, null, parameters, null) ?? throw new InvalidOperationException("Text binding unavailable.");
    }
}
