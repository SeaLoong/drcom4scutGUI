using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace drcom4scutGUI;

/// <summary>Finds credentials in the original text and replaces their combined spans.</summary>
internal sealed class CredentialRedactor
{
    [Flags]
    private enum Kind { Account = 1, Password = 2 }
    private readonly (string Value, Kind Kind)[] patterns;

    internal CredentialRedactor(string username, string password)
    {
        Dictionary<string, Kind> values = new(StringComparer.Ordinal);
        void Add(string value, Kind kind)
        {
            if (value.Length == 0) return;
            values[value] = values.GetValueOrDefault(value) | kind;
        }
        void AddForms(string value, Kind kind)
        {
            Add(value, kind);
            Add(JsonEncodedText.Encode(value).ToString(), kind);
            Add(Uri.EscapeDataString(value), kind);
        }
        AddForms(username, Kind.Account);
        AddForms(password, Kind.Password);
        patterns = values.Select(value => (value.Key, value.Value)).ToArray();
    }

    internal string Redact(string text)
    {
        if (patterns.Length == 0) return text;
        int[] positions = patterns.Select(pattern => text.IndexOf(pattern.Value, StringComparison.Ordinal)).ToArray();
        StringBuilder? result = null;
        int spanStart = -1, spanEnd = -1, copied = 0;
        Kind spanKind = 0;
        void Emit()
        {
            result!.Append(text, copied, spanStart - copied);
            result.Append(spanKind switch
            {
                Kind.Account => "[账号已隐藏]",
                Kind.Password => "[密码已隐藏]",
                _ => "[凭据已隐藏]"
            });
            copied = spanEnd;
        }
        while (true)
        {
            int next = int.MaxValue;
            foreach (int position in positions)
                if (position >= 0) next = Math.Min(next, position);
            if (next == int.MaxValue) break;
            result ??= new StringBuilder();
            if (spanStart < 0 || next > spanEnd)
            {
                if (spanStart >= 0) Emit();
                spanStart = spanEnd = next;
                spanKind = 0;
            }
            for (int i = 0; i < patterns.Length; i++)
            {
                if (positions[i] != next) continue;
                spanEnd = Math.Max(spanEnd, next + patterns[i].Value.Length);
                spanKind |= patterns[i].Kind;
                // Advance one character so overlapping occurrences in the
                // original text also belong to the same hidden span.
                positions[i] = text.IndexOf(patterns[i].Value, next + 1, StringComparison.Ordinal);
            }
        }
        if (result is null) return text;
        Emit();
        return result.Append(text, copied, text.Length - copied).ToString();
    }
}
