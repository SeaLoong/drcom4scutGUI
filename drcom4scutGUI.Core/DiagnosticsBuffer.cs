using System;
using System.Collections.Generic;

namespace drcom4scutGUI;

/// <summary>Bounds long-running UI logs and omits account identifiers in exported text.</summary>
public sealed class DiagnosticsBuffer(int capacity = 200)
{
    private readonly Queue<string> lines = new();
    private readonly object gate = new();

    public void Add(string line)
    {
        lock (gate)
        {
            lines.Enqueue(line.Length <= 2048 ? line : line[..2048] + "…");
            while (lines.Count > Math.Max(1, capacity)) lines.Dequeue();
        }
    }

    public string Export(string username = "", string password = "")
    {
        string text;
        lock (gate) text = string.Join(Environment.NewLine, lines);
        if (password.Length > 0) text = text.Replace(password, "[密码已隐藏]", StringComparison.Ordinal);
        if (username.Length > 0) text = text.Replace(username, "[账号已隐藏]", StringComparison.Ordinal);
        return text;
    }
}
