using System;
using Newtonsoft.Json.Linq;
using RimPilot.Json;

namespace RimPilot.Protocol
{
    // Explicit wire DTO validation. Returned tree contains only named, typed, bounded primitives.
    // This does NOT audit game getter eligibility: producers still require visibility projection first.
    internal static class ResponseContracts
    {
        internal static JObject Read(byte[] bytes, ReadMethod method, string expectedId)
        {
            try
            {
                JToken token = StrictJson.ParseResponse(bytes, method == ReadMethod.Screenshot);
                var f = new JsonFields(token, "v", "id", "ok", "data", "error");
                if (f.Integer("v", 1, 1) != 1) Reject();
                if (f.Has("id")) { string id = f.String("id", 64); if (!Request.ValidRequestId(id) || id != expectedId) Reject(); }
                else if (method != ReadMethod.Auth) Reject();
                if (f.Boolean("ok"))
                {
                    if (!f.Has("id") || f.Has("error")) Reject();
                    Data(f.Required("data"), method);
                }
                else
                {
                    if (f.Has("data")) Reject();
                    var error = new JsonFields(f.Required("error"), "code");
                    string code = error.String("code", 64);
                    bool known = false;
                    foreach (WireError item in Enum.GetValues(typeof(WireError))) known |= Responses.ErrorName(item) == code;
                    if (!known) Reject();
                }
                return JsonFields.Object(token);
            }
            catch (StrictJsonException) { throw new WireException(WireError.InvalidRequest); }
        }

        private static void Data(JToken token, ReadMethod method)
        {
            JsonFields f;
            switch (method)
            {
                case ReadMethod.Auth:
                    f = new JsonFields(token, "session", "protocol");
                    Ids.Read(f, "session", "s"); f.Integer("protocol", 1, 1); break;
                case ReadMethod.Health:
                    f = new JsonFields(token, "state", "scope");
                    Choice(f.String("state", 16), "bootstrap", "loading", "ready", "stopping");
                    if (f.Has("scope")) Scope.Read(f.Required("scope")); break;
                case ReadMethod.Capabilities:
                    f = new JsonFields(token, "capabilities");
                    var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                    foreach (JToken item in JsonFields.Array(f.Required("capabilities"), 64))
                    {
                        var c = new JsonFields(item, "name", "state", "schema", "reason");
                        if (!names.Add(c.String("name", 64))) Reject();
                        Choice(c.String("state", 16), "available", "unsupported", "unverified"); c.String("schema", 64);
                        if (c.Has("reason")) c.String("reason", 256);
                    }
                    break;
                case ReadMethod.Summary:
                    f = new JsonFields(token, "sample", "scene", "build", "paused", "currentMap", "maps", "dlcs", "mods");
                    Sample(f.Required("sample")); Choice(f.String("scene", 16), "menu", "playing"); f.String("build", 512);
                    if (f.Has("paused")) f.Boolean("paused");
                    if (f.Has("currentMap")) Ids.Read(f, "currentMap", "m");
                    if (f.Has("maps")) foreach (JToken item in JsonFields.Array(f.Required("maps"), 64))
                    {
                        var m = new JsonFields(item, "map", "label", "width", "height", "eligibleOwnPawns");
                        Ids.Read(m, "map", "m"); m.String("label", 512);
                        if (m.Has("width")) m.Integer("width", 1, 4096);
                        if (m.Has("height")) m.Integer("height", 1, 4096);
                        if (m.Has("eligibleOwnPawns")) m.Integer("eligibleOwnPawns", 0, WireLimits.SnapshotItems);
                    }
                    Manifest(f, "dlcs", 16); Manifest(f, "mods", 256); break;
                case ReadMethod.Pawn:
                    f = new JsonFields(token, "sample", "pawn");
                    Sample(f.Required("sample"), true); Pawn(f.Required("pawn"), true); break;
                case ReadMethod.Pawns:
                case ReadMethod.Things:
                    f = new JsonFields(token, "sample", "snapshot", "basis", "items", "complete", "next");
                    Sample(f.Required("sample"), true); Ids.Read(f, "snapshot", "q");
                    Choice(f.String("basis", 64), "eligible_observed_snapshot");
                    foreach (JToken item in JsonFields.Array(f.Required("items"), WireLimits.PageItems))
                        if (method == ReadMethod.Pawns) Pawn(item, false); else Thing(item);
                    bool complete = f.Boolean("complete");
                    if (f.Has("next") == complete) Reject();
                    if (f.Has("next")) Ids.Read(f, "next", "q"); break;
                case ReadMethod.Resources:
                    f = new JsonFields(token, "sample", "basis", "items"); Sample(f.Required("sample"), true);
                    Choice(f.String("basis", 64), "eligible_observed_direct_spawned_stack_units");
                    foreach (JToken item in JsonFields.Array(f.Required("items"), 256))
                    { var r = new JsonFields(item, "label", "stackUnits"); r.String("label", 512); r.Integer("stackUnits", 0, long.MaxValue); }
                    break;
                case ReadMethod.Ui:
                    f = new JsonFields(token, "sample", "selection", "windows"); Sample(f.Required("sample"));
                    if (f.Has("selection")) foreach (JToken item in JsonFields.Array(f.Required("selection"), 64))
                    {
                        var s = new JsonFields(item, "label", "pawn", "thing"); s.String("label", 512);
                        if (s.Has("pawn") && s.Has("thing")) Reject();
                        if (s.Has("pawn")) Ids.Read(s, "pawn", "p"); if (s.Has("thing")) Ids.Read(s, "thing", "t");
                    }
                    if (f.Has("windows")) foreach (JToken item in JsonFields.Array(f.Required("windows"), 32))
                    { var w = new JsonFields(item, "kind", "label"); w.String("kind", 64); if (w.Has("label")) w.String("label", 512); }
                    break;
                case ReadMethod.Events:
                    f = new JsonFields(token, "scope", "items", "next"); Scope.Read(f.Required("scope")); Ids.Read(f, "next", "e");
                    foreach (JToken item in JsonFields.Array(f.Required("items"), WireLimits.PageItems))
                    {
                        var e = new JsonFields(item, "kind", "label", "text");
                        string kind = e.String("kind", 16);
                        Choice(kind, "letter", "message", "lifecycle");
                        string text = e.String("text", 2048);
                        if (kind == "lifecycle")
                        {
                            if (e.Has("label")) Reject();
                            Choice(text, "observation_ready", "scope_changed", "observation_resumed");
                        }
                        else if (e.Has("label")) e.String("label", 512);
                    }
                    break;
                case ReadMethod.Screenshot: Capture(token); break;
                default: Reject(); break;
            }
        }
        private static void Sample(JToken token, bool mapRequired = false)
        {
            var f = new JsonFields(token, "scope", "capture", "frame", "phase", "tick");
            Scope scope = Scope.Read(f.Required("scope"));
            if (mapRequired && scope.Map == null) Reject();
            Ids.Read(f, "capture", "c"); f.Integer("frame", 0, long.MaxValue);
            Choice(f.String("phase", 32), "root_update_postfix");
            if (f.Has("tick")) { if (scope.Game == null) Reject(); f.Integer("tick", 0, long.MaxValue); }
        }
        private static void Manifest(JsonFields parent, string name, int max)
        {
            if (!parent.Has(name)) return;
            foreach (JToken item in JsonFields.Array(parent.Required(name), max))
            { var f = new JsonFields(item, "id", "label", "version"); f.String("id", 256); f.String("label", 512); if (f.Has("version")) f.String("version", 64); }
        }
        private static void Pawn(JToken token, bool detail)
        {
            var f = detail ? new JsonFields(token, "pawn", "label", "status", "needs", "health", "gear", "job", "lordReport")
                : new JsonFields(token, "pawn", "label", "status");
            Ids.Read(f, "pawn", "p"); f.String("label", 512);
            if (f.Has("status")) foreach (JToken item in JsonFields.Array(f.Required("status"), 16)) JsonFields.String(item, 512);
            if (!detail) return;
            if (f.Has("needs")) foreach (JToken item in JsonFields.Array(f.Required("needs"), 32))
            {
                var n = new JsonFields(item, "label", "level"); n.String("label", 512);
                JToken level = n.Required("level");
                if (level.Type != JTokenType.Float && level.Type != JTokenType.Integer) Reject();
                double number = (double)level; if (double.IsNaN(number) || double.IsInfinity(number) || number < 0 || number > 1) Reject();
            }
            if (f.Has("health")) foreach (JToken item in JsonFields.Array(f.Required("health"), 64))
            { var h = new JsonFields(item, "label", "part"); h.String("label", 512); if (h.Has("part")) h.String("part", 512); }
            if (f.Has("gear")) foreach (JToken item in JsonFields.Array(f.Required("gear"), 64))
            { var g = new JsonFields(item, "label", "slot"); g.String("label", 512); Choice(g.String("slot", 16), "apparel", "equipment"); }
            if (f.Has("job")) f.String("job", 512); if (f.Has("lordReport")) f.String("lordReport", 512);
        }
        private static void Thing(JToken token)
        {
            var f = new JsonFields(token, "thing", "label", "x", "z", "stackUnits");
            Ids.Read(f, "thing", "t"); f.String("label", 512); f.Integer("x", 0, 4095); f.Integer("z", 0, 4095);
            if (f.Has("stackUnits")) f.Integer("stackUnits", 1, int.MaxValue);
        }
        private static void Capture(JToken token)
        {
            var f = new JsonFields(token, "scope", "capture", "frame", "phase", "width", "height", "scale", "encoding", "bytes", "chunks");
            Scope.Read(f.Required("scope")); Ids.Read(f, "capture", "c"); f.Integer("frame", 0, long.MaxValue);
            Choice(f.String("phase", 32), "completed_render"); Choice(f.String("encoding", 32), "png_base64_chunks");
            long width = f.Integer("width", 1, WireLimits.CaptureWidth), height = f.Integer("height", 1, WireLimits.CaptureHeight);
            if (width * height > WireLimits.CapturePixels) Reject();
            JToken scaleToken = f.Required("scale");
            if (scaleToken.Type != JTokenType.Float && scaleToken.Type != JTokenType.Integer) Reject();
            double scale = (double)scaleToken;
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 || scale > 8) Reject();
            int count = (int)f.Integer("bytes", 8, WireLimits.CapturePngBytes);
            JArray chunks = JsonFields.Array(f.Required("chunks"), 256);
            if (chunks.Count == 0) Reject();
            int encodedChars = 0;
            int paddingChars = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                // JsonFields.String intentionally remains request-size-only. This one field has
                // dedicated larger response quota; do not widen ordinary DTO strings to match it.
                JToken chunk = chunks[i]; if (chunk.Type != JTokenType.String) Reject();
                string text = (string)chunk!;
                if (text.Length == 0 || text.Length > WireLimits.CaptureChunkChars || (i != chunks.Count - 1 && text.Length != WireLimits.CaptureChunkChars)) Reject();
                for (int c = 0; c < text.Length; c++)
                {
                    char ch = text[c];
                    bool padding = i == chunks.Count - 1 && c >= text.Length - 2 && ch == '=';
                    if (!padding && !(ch >= 'A' && ch <= 'Z') && !(ch >= 'a' && ch <= 'z') && !(ch >= '0' && ch <= '9') && ch != '+' && ch != '/') Reject();
                    if (ch == '=') paddingChars++;
                    if (ch == '=' && c != text.Length - 1 && text[c + 1] != '=') Reject();
                }
                encodedChars += text.Length;
            }
            if (encodedChars != ((count + 2) / 3) * 4 || encodedChars / 4 * 3 - paddingChars != count) Reject();
            // Decode once in CLI then validate PNG IHDR/dimensions/content. Not performed by schema.
        }
        private static void Choice(string value, params string[] choices)
        {
            foreach (string choice in choices) if (value == choice) return;
            Reject();
        }
        private static void Reject() { throw new WireException(WireError.InvalidRequest); }
    }
}
