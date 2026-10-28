using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimPilot.Protocol;

namespace RimPilot.Cli;

internal static class CliCommands
{
    internal static async Task<int> RunAsync(string[] args, string? runtimeDirectory, TextWriter output, TextWriter error)
    {
        ObservationCommand command;
        try { command = ObservationCommand.Parse(args); }
        catch (ArgumentException)
        {
            await error.WriteLineAsync("rim-pilot: usage: {sessions|health|capabilities|summary|pawns.list|pawn.get|things.list|resources|ui|events.read|screenshot} [--session SESSION] [--root ROOT --game GAME --map MAP] [--pawn PAWN] [--limit 1..64 --cursor CURSOR] [--output LOCAL_PATH]");
            return 64;
        }
        try
        {
            using var discovery = new SessionDiscovery(runtimeDirectory);
            JObject result;
            if (args[0] != "sessions")
            {
                using AuthenticatedSession peer = await discovery.ConnectAsync(command.Session);
                JObject response = command.Bootstrap ? await peer.ReadBootstrapAsync(command.Method)
                    : await peer.ReadObservationAsync(command);
                if (command.Method == ReadMethod.Screenshot && (bool)response["ok"]!)
                {
                    byte[] png = PngOutput.Decode(response["data"]!);
                    PngOutput.Write(command.Output!, png);
                    // Do not print megabytes of encoded image after local publication.
                    ((JObject)response["data"]!).Remove("chunks");
                }
                // CLI wrapper identifies the exact authenticated target even when health has no
                // observation scope. Only validated wire data reaches stdout.
                result = new JObject { ["v"] = WireLimits.Version, ["session"] = peer.Session, ["response"] = response };
                await output.WriteLineAsync(result.ToString(Formatting.None));
                if ((bool)response["ok"]!) return 0;
                await error.WriteLineAsync("rim-pilot: " + (string)response["error"]!["code"]! + ".");
                return 69;
            }
            if (command.Session != null)
            {
                using AuthenticatedSession peer = await discovery.ConnectAsync(command.Session);
                result = new JObject { ["v"] = 1, ["sessions"] = new JArray(new JObject {
                    ["v"] = 1, ["session"] = peer.Session, ["status"] = "live" }) };
            }
            else result = await discovery.ListAsync();
            await output.WriteLineAsync(result.ToString(Formatting.None));
            foreach (JToken item in (JArray)result["sessions"]!)
                if ((string?)item["status"] == "live") return 0;
            await error.WriteLineAsync("rim-pilot: no_live_session.");
            return 69;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or WireException or ArgumentException or NotSupportedException)
        {
            await error.WriteLineAsync("rim-pilot: observation_or_output_rejected.");
            return 69;
        }
        catch (SessionException failure)
        {
            // Fixed diagnostics only: no rejected arguments, descriptor fields or native messages.
            string code = failure.Code switch {
                SessionError.RegistryRejected => "registry_rejected", SessionError.ConflictingRecords => "conflicting_records",
                SessionError.NoLiveSession => "no_live_session", SessionError.SelectionRequired => "selection_required",
                SessionError.SessionNotFound => "session_not_found", SessionError.LimitExceeded => "discovery_limit_exceeded",
                _ => "session_unavailable" };
            await error.WriteLineAsync("rim-pilot: " + code + ".");
            return 69;
        }
    }
}
