using System;
using System.Text.Json;

namespace drcom4scutGUI;

public enum CoreConnectionPhase
{
    WaitingInterface,
    EapAuthentication,
    UdpAuthentication,
    PortalAuthentication,
    Online,
    Offline
}

public sealed record CoreConnectionStatus(CoreConnectionPhase Phase)
{
    public bool IsOnline => Phase == CoreConnectionPhase.Online;
}

/// <summary>Only the owned core's structured session state determines connectivity.</summary>
public static class AuthenticationStatus
{
    public const int MaximumStatusBytes = 16 * 1024;

    public static CoreConnectionStatus? Parse(ReadOnlyMemory<byte> data, int ownedPid, string mode, bool noUdp)
    {
        if (data.Length is 0 or > MaximumStatusBytes || ownedPid <= 0 || mode is not ("eap" or "portal")) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(data);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schema", out JsonElement schema) || !schema.TryGetInt32(out int version) || version != 1
                || !root.TryGetProperty("pid", out JsonElement pid) || !pid.TryGetInt32(out int processId) || processId != ownedPid
                || !root.TryGetProperty("phase", out JsonElement phase) || phase.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("interface", out JsonElement adapter) || adapter.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("online", out JsonElement online) || online.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("eap_authenticated", out JsonElement eap) || eap.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;

            bool portal = mode == "portal";
            // EAP uses the OS interface name, which may itself be "portal".
            // Provider identity comes from the launch options and phase/flags.
            if (portal && adapter.GetString() != "portal") return null;
            bool isOnline = online.GetBoolean();
            bool authorized = eap.GetBoolean();
            CoreConnectionPhase? result = phase.GetString() switch
            {
                "online" when isOnline && authorized != portal && (portal || adapter.GetString()!.Length > 0) => CoreConnectionPhase.Online,
                "offline" when !isOnline && !authorized => CoreConnectionPhase.Offline,
                "waiting-interface" when !portal && !isOnline && !authorized => CoreConnectionPhase.WaitingInterface,
                "eap-authentication" when !portal && !isOnline && !authorized => CoreConnectionPhase.EapAuthentication,
                "udp-authentication" when !portal && !noUdp && !isOnline && authorized => CoreConnectionPhase.UdpAuthentication,
                "portal-authentication" when portal && !isOnline && !authorized => CoreConnectionPhase.PortalAuthentication,
                _ => null
            };
            return result is CoreConnectionPhase value ? new CoreConnectionStatus(value) : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
