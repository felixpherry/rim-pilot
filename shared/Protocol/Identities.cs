using System;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using RimPilot.Json;

namespace RimPilot.Protocol
{
    internal static class Ids
    {
        internal static string New(string kind)
        {
            if (kind.Length != 1 || "srgmcptqe".IndexOf(kind, StringComparison.Ordinal) < 0)
                throw new ArgumentException("Unknown bridge identity kind.", nameof(kind));
            return kind + "_" + RandomHex(16);
        }
        internal static string NewToken() { return RandomHex(32); }
        private static string RandomHex(int count)
        {
            var bytes = new byte[count];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            const string hex = "0123456789abcdef";
            var chars = new char[count * 2];
            for (int i = 0; i < count; i++) { chars[i * 2] = hex[bytes[i] >> 4]; chars[i * 2 + 1] = hex[bytes[i] & 15]; }
            return new string(chars);
        }
        internal static bool Is(string value, string kind)
        {
            return value != null && value.Length == 34 && value.StartsWith(kind + "_", StringComparison.Ordinal) && IsHex(value.Substring(2), 32);
        }
        internal static bool IsHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            int invalid = 0;
            foreach (char c in value) invalid |= ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) ? 0 : 1;
            return invalid == 0;
        }
        internal static string Read(JsonFields fields, string name, string kind)
        {
            string value = fields.String(name, 34);
            if (!Is(value, kind)) throw new WireException(WireError.InvalidRequest);
            return value;
        }
    }

    internal sealed class Scope
    {
        internal string Session { get; }
        internal string Root { get; }
        internal string? Game { get; }
        internal string? Map { get; }
        internal Scope(string session, string root, string? game = null, string? map = null)
        {
            if (!Ids.Is(session, "s") || !Ids.Is(root, "r") || (game != null && !Ids.Is(game, "g"))
                || (map != null && (!Ids.Is(map, "m") || game == null))) throw new WireException(WireError.InvalidRequest);
            Session = session; Root = root; Game = game; Map = map;
        }
        internal static Scope Read(JToken token)
        {
            var f = new JsonFields(token, "session", "root", "game", "map");
            return new Scope(Ids.Read(f, "session", "s"), Ids.Read(f, "root", "r"),
                f.Has("game") ? Ids.Read(f, "game", "g") : null, f.Has("map") ? Ids.Read(f, "map", "m") : null);
        }
        internal JObject Json()
        {
            var value = new JObject { ["session"] = Session, ["root"] = Root };
            if (Game != null) value["game"] = Game;
            if (Map != null) value["map"] = Map;
            return value;
        }
        internal bool Same(Scope other)
        {
            return Session == other.Session && Root == other.Root && Game == other.Game && Map == other.Map;
        }
    }

    internal static class Readiness
    {
        // All booleans sampled on game thread. Long-event input means AnyEventNowOrWaiting,
        // NOT ShouldWaitForEvent. Base Root.Update postfix precedes derived play updates;
        // product samples after the complete derived Update, not render completion.
        internal static WireError? Check(bool anyEventNowOrWaiting, bool validRoot, bool transitioning,
            bool gameRequired, bool validGame, bool mapRequired, bool validMap, bool developerUnsafe)
        {
            if (developerUnsafe) return WireError.ObservationRefused;
            if (anyEventNowOrWaiting || !validRoot || transitioning || (gameRequired && !validGame) || (mapRequired && !validMap))
                return WireError.NotReady;
            return null;
        }
    }

    internal sealed class SampleStamp
    {
        internal Scope Scope { get; }
        internal string Capture { get; }
        internal long Frame { get; }
        internal long? Tick { get; }
        internal SampleStamp(Scope scope, long frame, long? tick)
        {
            if (frame < 0 || (tick.HasValue && (tick < 0 || scope.Game == null))) throw new WireException(WireError.InvalidRequest);
            Scope = scope; Frame = frame; Tick = tick; Capture = Ids.New("c");
        }
        internal JObject Json()
        {
            var result = new JObject { ["scope"] = Scope.Json(), ["capture"] = Capture,
                ["frame"] = Frame, ["phase"] = "root_update_postfix" };
            if (Tick.HasValue) result["tick"] = Tick.Value;
            return result;
        }
    }
}
