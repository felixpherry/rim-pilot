using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimPilot.Protocol;

namespace RimPilot.Cli;

internal enum SessionError { RegistryRejected, ConflictingRecords, NoLiveSession, SelectionRequired, SessionNotFound, SessionUnavailable, LimitExceeded }

internal sealed class SessionException : Exception
{
    internal SessionError Code { get; }
    internal SessionException(SessionError code) : base("Local session unavailable.") { Code = code; }
}

// One command owns one registry scan. No cache, retry, alternate registry or stale-file deletion.
internal sealed class SessionDiscovery : IDisposable
{
    private readonly LinuxPrivateDirectory registry;
    private readonly DiscoveryBudget budget = new();
    private readonly List<DiscoveryDescriptor> records = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private bool used, disposed;

    internal SessionDiscovery(string? runtimeDirectory)
    {
        LinuxPrivateDirectory? opened = null;
        try
        {
            opened = LinuxPrivateDirectory.Open(runtimeDirectory);
            var sessions = new HashSet<string>(StringComparer.Ordinal);
            var endpoints = new HashSet<int>();
            foreach (string name in opened.EnumerateNames(budget))
            {
                // Private native publication/quarantine workspaces are not descriptors. Any JSON
                // record is validated, including malformed/alias filenames; never hide ambiguity.
                if (!name.EndsWith(".json", StringComparison.Ordinal)) continue;
                DiscoveryDescriptor descriptor = opened.ReadRecord(name, budget);
                if (!sessions.Add(descriptor.Session) || !endpoints.Add(descriptor.Port))
                    throw new SessionException(SessionError.ConflictingRecords);
                records.Add(descriptor);
            }
            registry = opened;
        }
        catch (DiscoveryException error)
        {
            opened?.Dispose();
            throw new SessionException(error.Code == DiscoveryError.LimitExceeded ? SessionError.LimitExceeded : SessionError.RegistryRejected);
        }
        catch { opened?.Dispose(); throw; }
    }

    internal async Task<JObject> ListAsync()
    {
        StartOperation();
        var items = new JArray();
        foreach (DiscoveryDescriptor descriptor in records)
        {
            using AuthenticatedSession? peer = await ProbeAsync(descriptor).ConfigureAwait(false);
            JObject item = descriptor.PublicJson();
            // Unavailable is NOT independently proven stale/dead. Busy, timeout, port reuse,
            // malformed response and authentication failure all retain the private record.
            item["status"] = peer == null ? "unavailable" : "live";
            items.Add(item);
        }
        CheckTotalDeadline();
        return new JObject { ["v"] = WireLimits.Version, ["sessions"] = items };
    }

    internal async Task<AuthenticatedSession> ConnectAsync(string? session)
    {
        StartOperation();
        DiscoveryDescriptor? selected = null;
        if (session == null)
        {
            if (records.Count == 0) throw new SessionException(SessionError.NoLiveSession);
            // Even an unavailable record might be a temporarily busy live instance. Do not guess.
            if (records.Count != 1) throw new SessionException(SessionError.SelectionRequired);
            selected = records[0];
        }
        else
        {
            if (!Ids.Is(session, "s")) throw new SessionException(SessionError.SessionNotFound);
            selected = records.Find(value => value.Session == session);
            if (selected == null) throw new SessionException(SessionError.SessionNotFound);
        }
        AuthenticatedSession? peer = await ProbeAsync(selected).ConfigureAwait(false);
        try
        {
            CheckTotalDeadline();
            return peer ?? throw new SessionException(SessionError.SessionUnavailable);
        }
        catch { peer?.Dispose(); throw; }
    }

    private void StartOperation()
    {
        if (used || disposed) throw new SessionException(SessionError.SessionUnavailable);
        used = true;
        clock.Restart(); // One monotonic clock for the complete serial probe operation.
    }

    private void CheckTotalDeadline()
    {
        if (clock.ElapsedMilliseconds >= DiscoveryLimits.TotalProbeTimeoutMs)
            throw new SessionException(SessionError.LimitExceeded);
    }

    private async Task<AuthenticatedSession?> ProbeAsync(DiscoveryDescriptor descriptor)
    {
        int allowance;
        try { allowance = budget.BeginProbe(clock.ElapsedMilliseconds); }
        catch (DiscoveryException) { throw new SessionException(SessionError.LimitExceeded); }
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        SocketLifetime? lifetime = null;
        try
        {
            socket.NoDelay = true;
            socket.SendBufferSize = WireLimits.SocketBufferBytes;
            socket.ReceiveBufferSize = WireLimits.SocketBufferBytes;
            lifetime = new SocketLifetime(socket, allowance);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, descriptor.Port), lifetime.Cancellation).ConfigureAwait(false);
            using var stream = new NetworkStream(socket, false);
            string id = "discovery";
            byte[] auth = WireJson.Encode(new JObject { ["v"] = WireLimits.Version, ["id"] = id,
                ["method"] = "auth", ["params"] = new JObject { ["session"] = descriptor.Session, ["token"] = descriptor.Token } });
            await Framing.WriteAsync(stream, auth, WireLimits.RequestBytes, lifetime.Cancellation).ConfigureAwait(false);
            byte[] bytes = await Framing.ReadAsync(stream, WireLimits.ResponseBytes, lifetime.Cancellation).ConfigureAwait(false);
            lifetime.EnsureLive();
            JObject response = ResponseContracts.Read(bytes, ReadMethod.Auth, id);
            if ((bool)response["ok"]! != true || (string?)response["data"]?["session"] != descriptor.Session)
                return null;
            CheckTotalDeadline();
            lifetime.EnsureLive();
            // Transfer this SAME authenticated socket. A later command must never probe then
            // reconnect, adopt a replaced descriptor, retry or silently target another instance.
            var peer = new AuthenticatedSession(descriptor.Session, socket, lifetime);
            lifetime = null;
            socket = null!;
            return peer;
        }
        catch (Exception error) when (error is SocketException or IOException or OperationCanceledException or ObjectDisposedException or WireException)
        { return null; }
        finally { lifetime?.Dispose(); socket?.Dispose(); }
    }

    public void Dispose() { disposed = true; registry.Dispose(); }
}

// Single-owner connection for bootstrap commands. No credential exposed or stored here.
internal sealed class AuthenticatedSession : IDisposable
{
    private readonly Socket socket;
    private readonly SocketLifetime lifetime;
    internal string Session { get; }
    internal AuthenticatedSession(string session, Socket socket, SocketLifetime lifetime)
    { Session = session; this.socket = socket; this.lifetime = lifetime; }
    internal NetworkStream BeginRequest()
    {
        // EnsureLive before Begin prevents revival of an expired probe/previous request deadline.
        lifetime.EnsureLive();
        lifetime.Begin(WireLimits.RequestTimeoutMs);
        return new NetworkStream(socket, false);
    }
    internal async Task<JObject> ReadBootstrapAsync(ReadMethod method)
    {
        // No arbitrary method/params pass-through and no reconnect after probe/command failure.
        string name = method switch { ReadMethod.Health => "health", ReadMethod.Capabilities => "capabilities",
            _ => throw new SessionException(SessionError.SessionUnavailable) };
        try
        {
            using NetworkStream stream = BeginRequest();
            byte[] request = WireJson.Encode(new JObject { ["v"] = WireLimits.Version, ["id"] = name,
                ["method"] = name, ["params"] = new JObject() });
            await Framing.WriteAsync(stream, request, WireLimits.RequestBytes, lifetime.Cancellation).ConfigureAwait(false);
            byte[] bytes = await Framing.ReadAsync(stream, WireLimits.ResponseBytes, lifetime.Cancellation).ConfigureAwait(false);
            lifetime.EnsureLive();
            JObject response = ResponseContracts.Read(bytes, method, name);
            if (method == ReadMethod.Health && response["data"]?["scope"] is JToken scope
                && Scope.Read(scope).Session != Session)
                throw new WireException(WireError.StaleScope);
            lifetime.EnsureLive();
            return response;
        }
        catch (Exception error) when (error is SocketException or IOException or OperationCanceledException or ObjectDisposedException or WireException)
        {
            lifetime.Close();
            throw new SessionException(SessionError.SessionUnavailable);
        }
    }
    internal SocketLifetime Lifetime => lifetime;
    public void Dispose() { lifetime.Dispose(); socket.Dispose(); }
    public override string ToString() => "Authenticated local session.";
}
