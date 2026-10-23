using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class DiagnosticsBufferTests
{
    [Fact]
    public void StoredDiagnosticsStayRedactedAfterAnAccountIsChangedOrDeleted()
    {
        var log = new DiagnosticsBuffer();
        log.Add("first-student: password=first-secret, retry", "first-student", "first-secret");
        log.Add("second-student: online", "second-student", "second-secret");
        string text = log.Export();
        Assert.DoesNotContain("first-student", text);
        Assert.DoesNotContain("first-secret", text);
        Assert.DoesNotContain("second-student", text);
        Assert.Contains("retry", text);
        Assert.Contains("online", text);
    }

    [Fact]
    public void RedactionPrecedesTruncationSoNoCredentialPrefixSurvives()
    {
        const string password = "synthetic-private-password";
        var log = new DiagnosticsBuffer();
        log.Add(new string('x', 2040) + password, password: password);
        string text = log.Export();
        Assert.DoesNotContain("synthetic", text);
        Assert.Contains("[密码已隐藏]", text);
    }

    [Fact]
    public void ExportKeepsUsefulContextButRedactsEveryCredentialOccurrence()
    {
        var log = new DiagnosticsBuffer();
        log.Add("student: connect, password=synthetic-secret");
        log.Add("synthetic-secret student: network timeout; retry in 15 seconds");
        string text = log.Export("student", "synthetic-secret");
        Assert.DoesNotContain("student", text);
        Assert.DoesNotContain("synthetic-secret", text);
        Assert.Contains("network timeout; retry in 15 seconds", text);
        Assert.Contains("[账号已隐藏]", text);
        Assert.Contains("[密码已隐藏]", text);
    }

    [Fact]
    public void LongRunningSessionDropsOldLinesRatherThanGrowingForever()
    {
        var log = new DiagnosticsBuffer(3);
        for (int i = 0; i < 1000; i++) log.Add("event:" + i);
        Assert.Equal(["event:997", "event:998", "event:999"], log.Export().Split(Environment.NewLine));
    }

    [Fact]
    public void ExtremelyLongExternalCoreLineHasBoundedStorage()
    {
        var log = new DiagnosticsBuffer();
        log.Add(new string('x', 100_000));
        string text = log.Export();
        Assert.True(text.Length < 2100);
        Assert.EndsWith("…", text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void NonpositiveCapacityStillRetainsNewestDiagnostic(int capacity)
    {
        var log = new DiagnosticsBuffer(capacity);
        log.Add("old");
        log.Add("new");
        Assert.Equal("new", log.Export());
    }

    [Fact]
    public void EmptyCredentialArgumentsDoNotAlterDiagnosticText()
    {
        var log = new DiagnosticsBuffer();
        log.Add("No credentials or adapter detected.");
        Assert.Equal("No credentials or adapter detected.", log.Export());
    }

    [Fact]
    public void ConcurrentStdoutAndStderrCallbacksProduceCompleteBoundedLines()
    {
        var log = new DiagnosticsBuffer(200);
        Parallel.For(0, 1000, i =>
        {
            log.Add("complete-line-" + i);
            _ = log.Export();
        });
        string[] lines = log.Export().Split(Environment.NewLine);
        Assert.Equal(200, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("complete-line-", line));
        Assert.Equal(200, lines.Distinct().Count());
    }
}
