# Real PC restart validation — 2026-09-09

The installed configured build was tested after a real Windows restart, while the speaker remained in USB input. The agent did not select Bluetooth, run a separate discovery helper, pair again, or modify speaker settings before the first panel read.

The controller was opened after login. Its first source query recorded:

```json
{
  "initialResolution": "not-found",
  "recoveryAttempted": true,
  "discoveryStarted": true,
  "targetAdvertisementObserved": true,
  "resolutionAttempts": 2,
  "resolved": true,
  "cleanupError": null
}
```

The subsequent `source get`, `eq get`, `eq custom-get`, and `volume get` all completed. Readback was **USB / Monitor / volume 12**, with no setting command sent.

This validates the new discovery fallback on a real post-reboot cache miss for this test setup. It does not establish every firmware/adapter combination, recovery after a speaker power cycle, or physical sleep/wake behavior. Windows Shell event 9707 subsequently confirmed that the registered tray-start command was invoked at 06:35:08, after the manual test launch at 06:35:02. The initial absence of a process was therefore not sufficient evidence of broken autostart. A separate first `--start-in-tray` host-process test remained running without opening the panel; an already-running instance makes a secondary tray-start exit quietly. No startup registration was changed during this check.

Raw local logs and personal device identifiers are intentionally excluded from this public note.