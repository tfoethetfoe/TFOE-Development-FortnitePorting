using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FortnitePorting.Models.Uefn;

public sealed class UefnRemoteNode
{
    public string NodeId { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string ProjectRoot { get; init; } = string.Empty;
    public string EngineVersion { get; init; } = string.Empty;
}

public sealed class UefnRemoteCommandResult
{
    public bool Success { get; init; }
    public string Result { get; init; } = string.Empty;
    public string Output { get; init; } = string.Empty;
    public string Raw { get; init; } = string.Empty;
    public UefnRemoteNode? Node { get; init; }
}

public sealed class UefnBridgeUnavailableException : Exception
{
    public UefnBridgeUnavailableException(string message) : base(message) { }
    public UefnBridgeUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Client for the Python remote execution feature that ships with Unreal Editor for Fortnite.
/// This is Epic's protocol, not a custom one: a UDP discovery ping on 239.0.0.1:6766, then a
/// TCP command channel that the editor connects back to. UEFN ships with this disabled, so the
/// plugin configures it and falls back to the queued/Tools-menu workflow when it is unavailable.
/// </summary>
public sealed class UefnRemoteExecutionClient
{
    private const int ProtocolVersion = 1;
    private const string ProtocolMagic = "ue_py";
    private const int DiscoveryPort = 6766;
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("239.0.0.1");

    public string NodeId { get; } = Guid.NewGuid().ToString();

    public Task<UefnRemoteNode?> ProbeAsync(string? projectHint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            using var socket = CreateDiscoverySocket();
            return Discover(socket, projectHint, timeout, cancellationToken);
        }, cancellationToken);
    }

    public Task<UefnRemoteCommandResult> RunCommandAsync(
        string python,
        string? projectHint,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => RunCommand(python, projectHint, commandTimeout, cancellationToken), cancellationToken);
    }

    private UefnRemoteCommandResult RunCommand(
        string python,
        string? projectHint,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        using var socket = CreateDiscoverySocket();
        var node = Discover(socket, projectHint, TimeSpan.FromSeconds(4), cancellationToken)
                   ?? throw new UefnBridgeUnavailableException(
                       "No UEFN editor answered. Open the project in UEFN, enable Python Editor Scripting and remote execution (Set Up UEFN Project), then reopen the project.");

        TcpListener? listener = null;
        TcpClient? client = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint) listener.LocalEndpoint).Port;

            var openConnection = Envelope("open_connection", node.NodeId, new JObject
            {
                ["command_ip"] = "127.0.0.1",
                ["command_port"] = port
            });

            for (var attempt = 0; attempt < 6 && client is null; attempt++)
            {
                SendBoth(socket, openConnection);

                var acceptDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (DateTime.UtcNow < acceptDeadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (listener.Pending())
                    {
                        client = listener.AcceptTcpClient();
                        client.NoDelay = true;
                        break;
                    }

                    Thread.Sleep(50);
                }
            }

            if (client is null)
                throw new UefnBridgeUnavailableException(
                    "UEFN was found but did not accept the command connection. Reopen the project after running Set Up UEFN Project, and make sure remote execution is enabled.");

            var command = Envelope("command", node.NodeId, new JObject
            {
                ["command"] = python,
                ["unattended"] = true,
                ["exec_mode"] = "ExecuteFile"
            });

            var stream = client.GetStream();
            stream.ReadTimeout = 1000;
            stream.WriteTimeout = 15000;

            var payload = Encoding.UTF8.GetBytes(command.ToString(Formatting.None));
            stream.Write(payload, 0, payload.Length);
            stream.Flush();

            var response = ReadCommandResult(stream, node, commandTimeout, cancellationToken);
            return new UefnRemoteCommandResult
            {
                Success = response.Success,
                Result = response.Result,
                Output = response.Output,
                Raw = response.Raw,
                Node = node
            };
        }
        finally
        {
            try { client?.Close(); } catch { /* ignored */ }
            try { listener?.Stop(); } catch { /* ignored */ }
        }
    }

    private static UefnRemoteCommandResult ReadCommandResult(
        NetworkStream stream,
        UefnRemoteNode node,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new byte[16 * 1024];
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    throw new UefnBridgeUnavailableException("UEFN closed the command connection before returning a result.");

                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            catch (IOException)
            {
                // Read timeout; loop until the overall deadline so long imports can finish.
                continue;
            }

            var text = builder.ToString();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            JObject message;
            try
            {
                message = JObject.Parse(text);
            }
            catch (JsonException)
            {
                // The result can span several TCP segments; keep reading.
                if (builder.Length > 32 * 1024 * 1024)
                    throw new UefnBridgeUnavailableException("UEFN returned an unexpectedly large command result.");
                continue;
            }

            if (!IsMessageType(message, "command_result"))
                continue;

            var destination = message.Value<string>("dest");
            if (!string.IsNullOrEmpty(destination) && !destination.Equals(node.NodeId, StringComparison.Ordinal))
                continue;

            var data = message["data"] as JObject;
            var success = data?.Value<bool>("success") ?? false;
            var result = data?["result"]?.ToString() ?? string.Empty;
            var output = new StringBuilder();

            if (data?["output"] is JArray outputItems)
            {
                foreach (var item in outputItems)
                {
                    var line = item is JObject lineObject
                        ? lineObject.Value<string>("output") ?? lineObject.ToString(Formatting.None)
                        : item.ToString();
                    if (!string.IsNullOrEmpty(line))
                        output.AppendLine(line);
                }
            }

            return new UefnRemoteCommandResult
            {
                Success = success,
                Result = result,
                Output = output.ToString().TrimEnd(),
                Raw = text,
                Node = node
            };
        }

        throw new UefnBridgeUnavailableException(
            $"Timed out after {timeout.TotalMinutes:0.#} minutes waiting for UEFN to finish importing. The import may still be running in the editor; check the UEFN Output Log and the Fortnite Porting history.");
    }

    private UefnRemoteNode? Discover(
        Socket socket,
        string? projectHint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var nodes = new List<UefnRemoteNode>();
        var buffer = new byte[65535];
        var deadline = DateTime.UtcNow + timeout;
        var lastPing = DateTime.MinValue;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if ((DateTime.UtcNow - lastPing).TotalMilliseconds >= 400)
            {
                SendBoth(socket, Envelope("ping"));
                lastPing = DateTime.UtcNow;
            }

            if (!socket.Poll(100_000, SelectMode.SelectRead))
                continue;

            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int length;
            try
            {
                length = socket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException)
            {
                continue;
            }

            if (length <= 0)
                continue;

            JObject message;
            try
            {
                message = JObject.Parse(Encoding.UTF8.GetString(buffer, 0, length));
            }
            catch (JsonException)
            {
                continue;
            }

            if (!message.Value<string>("magic")?.Equals(ProtocolMagic, StringComparison.Ordinal) == true)
                continue;
            if (!IsMessageType(message, "pong"))
                continue;

            var source = message.Value<string>("source");
            if (string.IsNullOrWhiteSpace(source) || source.Equals(NodeId, StringComparison.Ordinal))
                continue;

            var destination = message.Value<string>("dest");
            if (!string.IsNullOrEmpty(destination) && !destination.Equals(NodeId, StringComparison.Ordinal))
                continue;

            var data = message["data"] as JObject;
            var node = new UefnRemoteNode
            {
                NodeId = source,
                ProjectName = data?.Value<string>("project_name") ?? data?.Value<string>("project_title") ?? string.Empty,
                ProjectRoot = data?.Value<string>("project_root") ?? string.Empty,
                EngineVersion = data?.Value<string>("engine_version") ?? string.Empty
            };

            if (!nodes.Any(existing => existing.NodeId == node.NodeId))
                nodes.Add(node);

            if (MatchesHint(node, projectHint))
                return node;
        }

        return nodes.FirstOrDefault();
    }

    private static bool IsMessageType(JObject message, string type)
        => message.Value<string>("type")?.Equals(type, StringComparison.Ordinal) == true;

    private static bool MatchesHint(UefnRemoteNode node, string? projectHint)
    {
        if (string.IsNullOrWhiteSpace(projectHint))
            return false;

        return node.ProjectName.Equals(projectHint, StringComparison.OrdinalIgnoreCase)
               || node.ProjectRoot.EndsWith(projectHint, StringComparison.OrdinalIgnoreCase)
               || projectHint.EndsWith(Path.GetFileName(node.ProjectRoot.TrimEnd('/', '\\')), StringComparison.OrdinalIgnoreCase);
    }

    private static Socket CreateDiscoverySocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 0);

            foreach (var address in GetLocalAddresses())
            {
                try
                {
                    socket.SetSocketOption(
                        SocketOptionLevel.IP,
                        SocketOptionName.AddMembership,
                        new MulticastOption(MulticastGroup, address));
                }
                catch
                {
                    // Some adapters reject multicast membership; the unicast fallback below still reaches the editor.
                }
            }

            return socket;
        }
        catch (Exception e)
        {
            socket.Dispose();
            throw new UefnBridgeUnavailableException(
                "UEFN's Python remote execution port (6766) is not available. Close other remote-execution tools or restart Fortnite Porting, then try again.", e);
        }
    }

    private static IEnumerable<IPAddress> GetLocalAddresses()
    {
        var addresses = new List<IPAddress> { IPAddress.Loopback };
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus is not OperationalStatus.Up)
                    continue;

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily is AddressFamily.InterNetwork &&
                        !addresses.Contains(unicast.Address))
                        addresses.Add(unicast.Address);
                }
            }
        }
        catch
        {
            // Fall back to loopback only.
        }

        return addresses;
    }

    private static void SendBoth(Socket socket, JObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));

        foreach (var endpoint in new[]
                 {
                     new IPEndPoint(MulticastGroup, DiscoveryPort),
                     new IPEndPoint(IPAddress.Loopback, DiscoveryPort)
                 })
        {
            try
            {
                socket.SendTo(bytes, endpoint);
            }
            catch (SocketException)
            {
                // The editor may only be reachable through one of the two routes.
            }
        }
    }

    private JObject Envelope(string type, string? destination = null, JObject? data = null)
    {
        var message = new JObject
        {
            ["version"] = ProtocolVersion,
            ["magic"] = ProtocolMagic,
            ["type"] = type,
            ["source"] = NodeId
        };

        if (!string.IsNullOrEmpty(destination))
            message["dest"] = destination;
        if (data is not null)
            message["data"] = data;

        return message;
    }
}
