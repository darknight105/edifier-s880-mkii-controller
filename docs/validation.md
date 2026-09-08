# Publication validation

Validated on Windows 11 x64 with .NET SDK 10.0.301 on 2026-09-08.

| Check | Result |
| --- | --- |
| Clean solution build without a speaker binding | Passed; zero warnings/errors |
| Unconfigured executable test suite | 62/62 passed |
| Rebuild with synthetic address `02:00:00:00:00:01` | Passed; zero warnings/errors |
| Configured executable test suite | 62/62 passed |
| Lightweight publish with matching CLI/GUI/profile binding | Passed |
| Packaged offline GUI and initial-refresh checks | Passed |
| Embedded payload hashes, backend launch, and IPC compatibility | Passed |
| Packaged lifecycle, second launch, and draft preservation | Passed |
| Public source scan for the original private address, user paths, and credential markers | No matches |
| Raw captures, APKs, bugreports, private logs, or personal binaries in publication | None |

The final checks above were run serially after cleaning project build outputs. Test addresses are synthetic; no real Bluetooth access occurred during this publication validation. See the README for the separate, earlier hardware observations and compatibility limits.

The optional self-contained publishing script was syntax-checked, but a self-contained binary was not built in this publication run. The lightweight path is the validated distribution path.