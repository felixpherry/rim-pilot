using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RimPilot.Json
{
    // Ingress ceilings, not framing/envelope schemas. Callers must bound the frame BEFORE allocation.
    internal static class JsonLimits
    {
        internal const int MaxBytes = 65536;
        internal const int MaxDepth = 16;
        internal const int MaxStringChars = 4096; // Decoded UTF-16 code units, including property names.
        internal const int MaxNumberChars = 64;
        internal const int MaxObjectProperties = 64;
        internal const int MaxArrayItems = 256;
        internal const int MaxNodes = 4096; // Values plus property names, across the entire document.
    }

    internal enum JsonFailure { Syntax, InvalidUtf8, Limit, Number, Field }

    internal sealed class StrictJsonException : Exception
    {
        internal JsonFailure Failure { get; }
        // Never retain input, field names, reader diagnostics, paths or inner exceptions (may contain credentials).
        internal StrictJsonException(JsonFailure failure) : base("JSON input rejected.") { Failure = failure; }
    }

    internal static class StrictJson
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static JToken Parse(byte[] input) { return Parse(input, JsonLimits.MaxBytes, JsonLimits.MaxStringChars); }

        // Response-only profiles. Requests/auth MUST use Parse, never these larger ceilings.
        internal static JToken ParseResponse(byte[] input, bool capture)
        {
            return Parse(input, capture ? 4194304 : 262144, capture ? 16384 : JsonLimits.MaxStringChars);
        }

        private static JToken Parse(byte[] input, int maxBytes, int maxStringChars)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (input.Length > maxBytes) throw new StrictJsonException(JsonFailure.Limit);
            string text;
            try { text = Utf8.GetString(input); }
            catch (DecoderFallbackException) { throw new StrictJsonException(JsonFailure.InvalidUtf8); }

            // JsonTextReader deliberately accepts non-JSON extensions. Validate grammar/quotas before it
            // allocates a token tree; duplicate names compare ordinally AFTER decoding escapes.
            new Grammar(text, maxStringChars).Validate();
            try
            {
                using (var source = new StringReader(text))
                using (var reader = new JsonTextReader(source)
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Double,
                    MaxDepth = JsonLimits.MaxDepth,
                    Culture = CultureInfo.InvariantCulture,
                    SupportMultipleContent = false
                })
                {
                    var token = JToken.Load(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        CommentHandling = CommentHandling.Load,
                        LineInfoHandling = LineInfoHandling.Ignore
                    });
                    if (reader.Read()) throw new StrictJsonException(JsonFailure.Syntax);
                    return token;
                }
            }
            catch (JsonException) { throw new StrictJsonException(JsonFailure.Syntax); }
            // No JsonSerializer/Deserialize/ToObject/TypeNameHandling or caller-provided converters.
        }

        private sealed class Grammar
        {
            private readonly string text;
            private readonly int maxStringChars;
            private int position;
            private int nodes;
            internal Grammar(string text, int maxStringChars) { this.text = text; this.maxStringChars = maxStringChars; }

            internal void Validate()
            {
                Space();
                Value(0);
                Space();
                if (position != text.Length) Fail();
            }

            private void Value(int depth)
            {
                Node();
                Space();
                char c = Peek();
                if (c == '{' || c == '[')
                {
                    if (depth >= JsonLimits.MaxDepth) Limit();
                    position++;
                    if (c == '{') Object(depth + 1); else Array(depth + 1);
                }
                else if (c == '"') String();
                else if (c == 't') Literal("true");
                else if (c == 'f') Literal("false");
                else if (c == 'n') Literal("null");
                else if (c == '-' || Digit(c)) Number();
                else Fail();
            }

            private void Object(int depth)
            {
                Space();
                if (Take('}')) return;
                var names = new HashSet<string>(StringComparer.Ordinal);
                int count = 0;
                do
                {
                    if (++count > JsonLimits.MaxObjectProperties) Limit();
                    Node();
                    Space();
                    string name = String();
                    if (!names.Add(name)) Fail();
                    Space();
                    Expect(':');
                    Value(depth);
                    Space();
                    if (Take('}')) return;
                    Expect(','); // Next iteration requires a name: trailing comma is not accepted.
                } while (true);
            }

            private void Array(int depth)
            {
                Space();
                if (Take(']')) return;
                int count = 0;
                do
                {
                    if (++count > JsonLimits.MaxArrayItems) Limit();
                    Value(depth);
                    Space();
                    if (Take(']')) return;
                    Expect(',');
                } while (true);
            }

            private string String()
            {
                Expect('"');
                var result = new StringBuilder();
                bool highSurrogate = false;
                while (position < text.Length)
                {
                    char c = text[position++];
                    if (c == '"')
                    {
                        if (highSurrogate) Fail();
                        return result.ToString();
                    }
                    if (c < 0x20) Fail();
                    if (c == '\\')
                    {
                        if (position == text.Length) Fail();
                        switch (text[position++])
                        {
                            case '"': c = '"'; break;
                            case '\\': c = '\\'; break;
                            case '/': c = '/'; break;
                            case 'b': c = '\b'; break;
                            case 'f': c = '\f'; break;
                            case 'n': c = '\n'; break;
                            case 'r': c = '\r'; break;
                            case 't': c = '\t'; break;
                            case 'u':
                                int code = 0;
                                for (int i = 0; i < 4; i++)
                                {
                                    if (position == text.Length) Fail();
                                    char hex = text[position++];
                                    int digit = hex >= '0' && hex <= '9' ? hex - '0' :
                                        hex >= 'a' && hex <= 'f' ? hex - 'a' + 10 :
                                        hex >= 'A' && hex <= 'F' ? hex - 'A' + 10 : -1;
                                    if (digit < 0) Fail();
                                    code = code * 16 + digit;
                                }
                                c = (char)code;
                                break;
                            default: Fail(); break;
                        }
                    }
                    if (highSurrogate ? !char.IsLowSurrogate(c) : char.IsLowSurrogate(c)) Fail();
                    highSurrogate = char.IsHighSurrogate(c);
                    if (result.Length == maxStringChars) Limit();
                    result.Append(c);
                }
                Fail();
                return ""; // Unreachable.
            }

            private void Number()
            {
                int start = position;
                Take('-');
                if (!Take('0'))
                {
                    if (Peek() < '1' || Peek() > '9') Fail();
                    Digits();
                }
                bool fractional = false;
                if (Take('.')) { fractional = true; RequireDigits(); }
                if (Take('e') || Take('E'))
                {
                    fractional = true;
                    if (!Take('+')) Take('-');
                    RequireDigits();
                }
                if (position - start > JsonLimits.MaxNumberChars) Limit();
                string number = text.Substring(start, position - start);
                if (fractional)
                {
                    if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                        || double.IsNaN(value) || double.IsInfinity(value))
                        throw new StrictJsonException(JsonFailure.Number);
                }
                else if (!long.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                    throw new StrictJsonException(JsonFailure.Number);
            }

            private void RequireDigits() { if (!Digit(Peek())) Fail(); Digits(); }
            private void Digits()
            {
                int start = position;
                while (Digit(Peek()))
                {
                    if (position - start >= JsonLimits.MaxNumberChars) Limit();
                    position++;
                }
            }
            private void Literal(string literal)
            {
                foreach (char c in literal) Expect(c);
            }
            private void Node() { if (++nodes > JsonLimits.MaxNodes) Limit(); }
            private static bool Digit(char c) { return c >= '0' && c <= '9'; }
            private char Peek() { return position < text.Length ? text[position] : '\0'; }
            private bool Take(char c) { if (Peek() != c) return false; position++; return true; }
            private void Expect(char c) { if (!Take(c)) Fail(); }
            private void Space()
            {
                while (position < text.Length && (text[position] == ' ' || text[position] == '\t'
                    || text[position] == '\n' || text[position] == '\r')) position++;
            }
            private static void Fail() { throw new StrictJsonException(JsonFailure.Syntax); }
            private static void Limit() { throw new StrictJsonException(JsonFailure.Limit); }
        }
    }
}
