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
## Recovery update — 2026-09-09

- Protocol and resolver suite: **69/69 passed**.
- Packaged offline GUI: passed, including finite retry success/exhaustion, pending-draft preservation, manual/exit cancellation, coalesced resume reads, and no replay of setting commands.
- Packaged payload and lifecycle: passed.
- Real Windows discovery after a simulated missing first lookup: passed; target advertisement observed, second lookup resolved, and watcher cleanup completed. This was not a real reboot.
- Installed configured build: actual first-open read passed in USB with Monitor and volume 12/30.
- Windows Classic audio Connect in USB: failed; no automatic Classic/A2DP reconnect is claimed.
- Real PC restart with the speaker left in USB: **passed** on 2026-09-09; first lookup missed, automatic discovery found the target, second lookup resolved, all four reads completed. See [reboot validation](reboot-validation.md).
- Physical sleep/wake and speaker power cycle: **pending**. Windows Shell logs confirmed delayed invocation of login autostart, and a separate first tray-start test remained running; see the reboot validation note.

The recovery code uses the same protocol frames and pre-write identity/confirmation rules. No persistent exclusive GATT lease, repeated pairing, radio toggle, or driver reset was added. Initial build warnings concerned unavailable NuGet vulnerability metadata; later publishing completed successfully. An online dependency security audit is not claimed.
