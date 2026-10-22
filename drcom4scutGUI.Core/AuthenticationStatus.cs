using System;

namespace drcom4scutGUI;

/// <summary>Each provider confirms success at its complete online state.</summary>
public static class AuthenticationStatus
{
    public static bool IsSuccessLine(string mode, bool noUdp, string line) => mode switch
    {
        "portal" => line.Contains("Portal authorization confirmed online.", StringComparison.Ordinal),
        "eap" when noUdp => line.Contains("802.1X Authorization success!", StringComparison.Ordinal),
        "eap" => line.Contains("UDP heartbeat completed.", StringComparison.Ordinal),
        _ => false
    };
}
