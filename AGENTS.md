# Working agreements

- Work in the existing isolated checkout; do not create a worktree unless requested.
- The user requires every completed iteration to be committed and pushed to `master`. Validate relevant changes, use normal non-forced pushes, and preserve remote changes.
- Never commit account configuration, plaintext or encrypted private credentials, logs, build caches, or generated release archives.
- Use the stable .NET 10 SDK selected by global.json and locked NuGet restores. This repository uses Microsoft.Testing.Platform: follow docs/development.md for the real test command and verify nonzero TRX results.
- Preserve CurrentUser DPAPI storage, private password stdin, explicit provider selection and graceful private control. Do not fall back to password arguments or environment variables.
- Use the native six-platform Avalonia matrix and headless UI tests. Cross-compilation or headless tests do not establish native window/tray, DPAPI/Keychain/Secret Service, Npcap/BPF or campus-network behavior; keep platform outcomes distinct.
