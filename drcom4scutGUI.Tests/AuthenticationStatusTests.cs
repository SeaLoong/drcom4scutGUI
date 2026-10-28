using System.Text;
using System.Text.Json;
using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class AuthenticationStatusTests
{
    [Theory]
    [InlineData("portal", false, "portal", "online", false, true, CoreConnectionPhase.Online)]
    [InlineData("eap", false, "eth0", "online", true, true, CoreConnectionPhase.Online)]
    [InlineData("eap", true, "eth0", "online", true, true, CoreConnectionPhase.Online)]
    [InlineData("eap", false, "portal", "online", true, true, CoreConnectionPhase.Online)]
    [InlineData("eap", true, "portal", "online", true, true, CoreConnectionPhase.Online)]
    [InlineData("eap", false, "eth0", "udp-authentication", true, false, CoreConnectionPhase.UdpAuthentication)]
    [InlineData("eap", false, "portal", "udp-authentication", true, false, CoreConnectionPhase.UdpAuthentication)]
    [InlineData("eap", false, "eth0", "eap-authentication", false, false, CoreConnectionPhase.EapAuthentication)]
    [InlineData("eap", false, "", "waiting-interface", false, false, CoreConnectionPhase.WaitingInterface)]
    [InlineData("portal", false, "portal", "portal-authentication", false, false, CoreConnectionPhase.PortalAuthentication)]
    [InlineData("eap", false, "eth0", "offline", false, false, CoreConnectionPhase.Offline)]
    [InlineData("eap", false, "portal", "offline", false, false, CoreConnectionPhase.Offline)]
    public void CompleteStructuredStateSelectsOnlyTheCurrentProvidersPhase(string mode, bool noUdp, string adapter,
        string phase, bool eap, bool online, CoreConnectionPhase expected)
        => Assert.Equal(expected, AuthenticationStatus.Parse(Status(17, adapter, phase, eap, online), 17, mode, noUdp)?.Phase);

    [Theory]
    [InlineData("eap", false, "eth0", "online", false, true)]
    [InlineData("eap", false, "eth0", "online", true, false)]
    [InlineData("portal", false, "portal", "online", true, true)]
    [InlineData("portal", false, "eth0", "online", false, true)]
    [InlineData("eap", false, "portal", "online", false, true)]
    [InlineData("eap", false, "portal", "portal-authentication", false, false)]
    [InlineData("eap", true, "eth0", "udp-authentication", true, false)]
    [InlineData("portal", false, "portal", "offline", false, true)]
    [InlineData("eap", false, "eth0", "online-next", true, true)]
    [InlineData("unknown", false, "eth0", "online", true, true)]
    public void ConflictingOrUnknownStatesCannotConfirmAuthentication(string mode, bool noUdp, string adapter,
        string phase, bool eap, bool online)
        => Assert.Null(AuthenticationStatus.Parse(Status(17, adapter, phase, eap, online), 17, mode, noUdp));

    [Fact]
    public void AnotherProcessAndUnsupportedSchemaAreRejected()
    {
        byte[] json = Status(18, "eth0", "online", true, true);
        Assert.Null(AuthenticationStatus.Parse(json, 17, "eap", false));
        json = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Status(17, "eth0", "online", true, true)).Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal));
        Assert.Null(AuthenticationStatus.Parse(json, 17, "eap", false));
    }

    [Theory]
    [InlineData("UDP heartbeat completed.")]
    [InlineData("[INFO] Server information: UDP heartbeat completed.")]
    [InlineData("[ERROR] EAP server notification: Portal authorization confirmed online.")]
    [InlineData("{}")]
    [InlineData("{\"schema\":\"1\"}")]
    [InlineData("{\"schema\":1,\"pid\":17,\"phase\":\"online\",\"online\":true}")]
    public void LogsAndIncompleteDocumentsAreNeverAuthenticationEvidence(string value)
        => Assert.Null(AuthenticationStatus.Parse(Encoding.UTF8.GetBytes(value), 17, "eap", false));

    [Fact]
    public void OversizedDocumentsAreRejectedBeforeParsing()
        => Assert.Null(AuthenticationStatus.Parse(new byte[AuthenticationStatus.MaximumStatusBytes + 1], 17, "eap", false));

    private static byte[] Status(int pid, string adapter, string phase, bool eap, bool online)
        => JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, pid, @interface = adapter, phase, eap_authenticated = eap, online });
}
