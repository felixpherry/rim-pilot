using System;
using Newtonsoft.Json.Linq;
using RimPilot.Json;

namespace RimPilot.Protocol
{
    internal enum ReadMethod { Auth, Health, Capabilities, Summary, Pawns, Pawn, Things, Resources, Ui, Screenshot, Events }

    // Transport validates concrete intents, never queued JTokens, client closures or method reflection.
    internal sealed class Request
    {
        internal string Id { get; private set; } = "";
        internal ReadMethod Method { get; private set; }
        internal Scope? Scope { get; private set; }
        internal string? Session { get; private set; }
        internal string? Token { get; private set; }
        internal string? Target { get; private set; }
        internal string? Cursor { get; private set; }
        internal int Limit { get; private set; }

        internal static Request Read(byte[] bytes)
        {
            try
            {
                var envelope = new JsonFields(StrictJson.Parse(bytes), "v", "id", "method", "params");
                if (envelope.Integer("v", 0, int.MaxValue) != WireLimits.Version) throw new WireException(WireError.VersionMismatch);
                var request = new Request { Id = envelope.String("id", 64), Method = MethodOf(envelope.String("method", 64)) };
                if (!ValidRequestId(request.Id)) throw new WireException(WireError.InvalidRequest);
                JToken args = envelope.Required("params");
                JsonFields f;
                switch (request.Method)
                {
                    case ReadMethod.Auth:
                        f = new JsonFields(args, "session", "token");
                        request.Session = Ids.Read(f, "session", "s");
                        request.Token = f.String("token", 64);
                        if (!Ids.IsHex(request.Token, 64)) throw new WireException(WireError.InvalidRequest);
                        break;
                    case ReadMethod.Health:
                    case ReadMethod.Capabilities:
                        new JsonFields(args);
                        break;
                    case ReadMethod.Pawns:
                    case ReadMethod.Things:
                    case ReadMethod.Events:
                        f = new JsonFields(args, "scope", "limit", "cursor");
                        request.Scope = Scope.Read(f.Required("scope"));
                        request.Limit = (int)f.Integer("limit", 1, WireLimits.PageItems);
                        if (f.Has("cursor")) request.Cursor = Ids.Read(f, "cursor", request.Method == ReadMethod.Events ? "e" : "q");
                        break;
                    case ReadMethod.Pawn:
                        f = new JsonFields(args, "scope", "pawn");
                        request.Scope = Scope.Read(f.Required("scope"));
                        request.Target = Ids.Read(f, "pawn", "p");
                        break;
                    default:
                        f = new JsonFields(args, "scope");
                        request.Scope = Scope.Read(f.Required("scope"));
                        break;
                }
                bool mapRequired = request.Method == ReadMethod.Pawns || request.Method == ReadMethod.Pawn
                    || request.Method == ReadMethod.Things || request.Method == ReadMethod.Resources;
                if (mapRequired && request.Scope?.Map == null) throw new WireException(WireError.InvalidRequest);
                return request;
            }
            catch (StrictJsonException error)
            {
                throw new WireException(error.Failure == JsonFailure.Field ? WireError.InvalidRequest : WireError.InvalidJson);
            }
        }

        internal static bool ValidRequestId(string id)
        {
            if (id.Length == 0 || id.Length > 64) return false;
            foreach (char c in id)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_' && c != '-') return false;
            return true;
        }
        private static ReadMethod MethodOf(string method)
        {
            switch (method)
            {
                case "auth": return ReadMethod.Auth;
                case "health": return ReadMethod.Health;
                case "capabilities": return ReadMethod.Capabilities;
                case "summary": return ReadMethod.Summary;
                case "pawns.list": return ReadMethod.Pawns;
                case "pawn.get": return ReadMethod.Pawn;
                case "things.list": return ReadMethod.Things;
                case "resources": return ReadMethod.Resources;
                case "ui": return ReadMethod.Ui;
                case "screenshot": return ReadMethod.Screenshot;
                case "events.read": return ReadMethod.Events;
                case "click": case "key": case "text": case "scroll": case "drag": case "pause":
                case "select": case "camera": case "save": case "load": case "debug": case "eval":
                case "action": case "grant": case "reflect": throw new WireException(WireError.ForbiddenMethod);
                default: throw new WireException(WireError.UnsupportedMethod);
            }
        }
    }

    internal sealed class Authentication
    {
        private readonly string session;
        private readonly string token;
        private bool attempted;
        internal bool Authenticated { get; private set; }
        internal Authentication(string session, string token)
        {
            if (!Ids.Is(session, "s") || !Ids.IsHex(token, 64)) throw new ArgumentException("Invalid local authentication material.");
            this.session = session; this.token = token;
        }
        internal void Accept(byte[] frame)
        {
            // One attempt per connection; caller closes on ALL failures. No parsing/state oracle pre-auth.
            if (attempted) throw new WireException(WireError.AuthenticationFailed);
            attempted = true;
            try
            {
                Request request = Request.Read(frame);
                bool matchToken = EqualSecret(token, request.Token ?? "");
                bool matchSession = string.Equals(session, request.Session, StringComparison.Ordinal);
                if (request.Method != ReadMethod.Auth || !matchToken || !matchSession)
                    throw new WireException(WireError.AuthenticationFailed);
                Authenticated = true;
            }
            catch (WireException) { throw new WireException(WireError.AuthenticationFailed); }
        }
        private static bool EqualSecret(string expected, string received)
        {
            // Fixed 64-character comparison for well-formed tokens. Format failures have same wire error.
            int different = expected.Length ^ received.Length;
            for (int i = 0; i < 64; i++) different |= expected[i] ^ (i < received.Length ? received[i] : 0);
            return different == 0;
        }
    }
}
