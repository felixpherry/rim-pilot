using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using RimPilot.Protocol;

namespace RimPilot.Cli;

// Closed command/argument surface; output path never enters a wire request.
internal sealed class ObservationCommand
{
    internal string Name { get; }
    internal ReadMethod Method { get; }
    internal string? Session, Root, Game, Map, Pawn, Cursor, Output;
    internal int Limit = WireLimits.PageItems;
    internal bool Bootstrap => Method == ReadMethod.Health || Method == ReadMethod.Capabilities;
    private ObservationCommand(string name, ReadMethod method) { Name = name; Method = method; }

    internal static ObservationCommand Parse(string[] args)
    {
        if (args.Length == 0 || args.Length % 2 != 1) throw new ArgumentException();
        ReadMethod method = args[0] switch {
            "sessions" => ReadMethod.Auth, "health" => ReadMethod.Health, "capabilities" => ReadMethod.Capabilities,
            "summary" => ReadMethod.Summary, "pawns.list" => ReadMethod.Pawns, "pawn.get" => ReadMethod.Pawn,
            "things.list" => ReadMethod.Things, "resources" => ReadMethod.Resources, "ui" => ReadMethod.Ui,
            "events.read" => ReadMethod.Events, "screenshot" => ReadMethod.Screenshot,
            _ => throw new ArgumentException() };
        var command = new ObservationCommand(args[0], method);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool paged = method is ReadMethod.Pawns or ReadMethod.Things or ReadMethod.Events;
        bool observation = !command.Bootstrap && method != ReadMethod.Auth;
        for (int i = 1; i < args.Length; i += 2)
        {
            string key = args[i], value = args[i + 1];
            if (!seen.Add(key)) throw new ArgumentException();
            switch (key)
            {
                case "--session": command.Session = value; break;
                case "--root" when observation && Ids.Is(value, "r"): command.Root = value; break;
                case "--game" when observation && Ids.Is(value, "g"): command.Game = value; break;
                case "--map" when observation && Ids.Is(value, "m"): command.Map = value; break;
                case "--pawn" when method == ReadMethod.Pawn && Ids.Is(value, "p"): command.Pawn = value; break;
                case "--cursor" when paged && Ids.Is(value, method == ReadMethod.Events ? "e" : "q"): command.Cursor = value; break;
                case "--limit" when paged && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int limit)
                    && limit >= 1 && limit <= WireLimits.PageItems: command.Limit = limit; break;
                case "--output" when method == ReadMethod.Screenshot && !string.IsNullOrWhiteSpace(value): command.Output = value; break;
                default: throw new ArgumentException();
            }
        }
        if (command.Root == null && (command.Game != null || command.Map != null)
            || command.Map != null && command.Game == null
            || method is ReadMethod.Pawns or ReadMethod.Pawn or ReadMethod.Things or ReadMethod.Resources && command.Map == null
            || method == ReadMethod.Pawn && command.Pawn == null
            || method == ReadMethod.Screenshot && command.Output == null) throw new ArgumentException();
        return command;
    }

    internal JObject Parameters(Scope scope)
    {
        var result = new JObject { ["scope"] = scope.Json() };
        if (Method is ReadMethod.Pawns or ReadMethod.Things or ReadMethod.Events)
        {
            result["limit"] = Limit;
            if (Cursor != null) result["cursor"] = Cursor;
        }
        if (Pawn != null) result["pawn"] = Pawn;
        return result;
    }
}
