using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace drcom4scutGUI;

/// <summary>Reads diagnostics with a fixed memory budget even without a newline.</summary>
internal static class CoreOutputReader
{
    internal const int MaximumLineCharacters = 16 * 1024;
    internal const int MaximumProbeCharacters = 64 * 1024;

    internal static async Task ReadLinesAsync(StreamReader stream, Action<string> onLine,
        CredentialRedactor redactor, CancellationToken cancellationToken = default)
    {
        char[] chunk = new char[4096];
        StringBuilder line = new();
        bool omitted = false;
        bool afterCarriageReturn = false;
        void Emit()
        {
            // Dropping an oversized line as a whole also avoids exposing a
            // password fragment cut at the length limit.
            string text = omitted ? "[核心日志过长，已省略]" : line.ToString();
            onLine(redactor.Redact(text));
            line.Clear();
            omitted = false;
        }
        int count;
        while ((count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                char value = chunk[i];
                if (value == '\n' && afterCarriageReturn) { afterCarriageReturn = false; continue; }
                afterCarriageReturn = value == '\r';
                if (value is '\r' or '\n') { Emit(); continue; }
                if (omitted) continue;
                if (line.Length == MaximumLineCharacters) { line.Clear(); omitted = true; }
                else line.Append(value);
            }
        }
        if (omitted || line.Length > 0) Emit();
    }

    internal static async Task<string> ReadProbeAsync(StreamReader stream, CancellationToken cancellationToken)
    {
        char[] chunk = new char[4096];
        StringBuilder output = new();
        int count;
        while ((count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > MaximumProbeCharacters)
                throw new InvalidDataException("核心自检输出过长，请检查安装目录中的核心程序。");
            output.Append(chunk, 0, count);
        }
        return output.ToString();
    }
}
