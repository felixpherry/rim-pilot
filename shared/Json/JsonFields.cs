using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimPilot.Json
{
    // Each DTO mapper explicitly lists fields, then reads typed primitives. No reflection or coercion.
    // This helper defines no envelope, authentication or observation schema.
    internal sealed class JsonFields
    {
        private readonly JObject value;

        internal JsonFields(JToken token, params string[] allowedFields)
        {
            value = Object(token);
            var allowed = new HashSet<string>(allowedFields, StringComparer.Ordinal);
            foreach (JProperty property in value.Properties())
                if (!allowed.Contains(property.Name)) Reject();
        }

        internal JToken Required(string name)
        {
            JToken? token = value.GetValue(name, StringComparison.Ordinal);
            if (token == null) Reject();
            return token!;
        }

        // Absent is distinct from explicit JSON null; the latter must be allowed by a concrete schema.
        internal bool Has(string name) { return value.GetValue(name, StringComparison.Ordinal) != null; }
        internal string String(string name, int maxChars) { return String(Required(name), maxChars); }
        internal long Integer(string name, long min, long max) { return Integer(Required(name), min, max); }
        internal bool Boolean(string name) { return Boolean(Required(name)); }

        internal static JObject Object(JToken token)
        {
            if (!(token is JObject result)) throw new StrictJsonException(JsonFailure.Field);
            return result;
        }

        internal static JArray Array(JToken token, int maxItems)
        {
            if (maxItems < 0 || maxItems > JsonLimits.MaxArrayItems) throw new ArgumentOutOfRangeException(nameof(maxItems));
            if (!(token is JArray result) || result.Count > maxItems) throw new StrictJsonException(JsonFailure.Field);
            return result;
        }

        internal static string String(JToken token, int maxChars)
        {
            if (maxChars < 0 || maxChars > JsonLimits.MaxStringChars) throw new ArgumentOutOfRangeException(nameof(maxChars));
            if (token.Type != JTokenType.String) Reject();
            string result = (string)token!;
            if (result.Length > maxChars) Reject();
            return result;
        }

        internal static long Integer(JToken token, long min, long max)
        {
            if (min > max) throw new ArgumentOutOfRangeException(nameof(max));
            if (token.Type != JTokenType.Integer) Reject();
            // Tokens must originate in StrictJson.Parse: its signed Int64 bound precedes materialization.
            long result = (long)token;
            if (result < min || result > max) Reject();
            return result;
        }

        internal static bool Boolean(JToken token)
        {
            if (token.Type != JTokenType.Boolean) Reject();
            return (bool)token;
        }

        private static void Reject() { throw new StrictJsonException(JsonFailure.Field); }
    }
}
