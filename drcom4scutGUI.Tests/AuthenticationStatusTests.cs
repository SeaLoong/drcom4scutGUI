using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class AuthenticationStatusTests
{
    [Theory]
    [InlineData("portal", false, "Portal authorization confirmed online.", true)]
    [InlineData("portal", false, "802.1X Authorization success!", false)]
    [InlineData("portal", false, "UDP heartbeat completed.", false)]
    [InlineData("eap", false, "Portal authorization confirmed online.", false)]
    [InlineData("eap", false, "802.1X Authorization success!", false)]
    [InlineData("eap", false, "UDP heartbeat completed.", true)]
    [InlineData("eap", true, "802.1X Authorization success!", true)]
    [InlineData("unknown", false, "Portal authorization confirmed online.", false)]
    public void SuccessRequiresTheSelectedProvidersCompleteOnlineState(string mode, bool noUdp, string line, bool expected)
        => Assert.Equal(expected, AuthenticationStatus.IsSuccessLine(mode, noUdp, line));
}
