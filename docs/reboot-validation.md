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

This validates the new discovery fallback on a real post-reboot cache miss for this test setup. It does not establish every firmware/adapter combination, recovery after a speaker power cycle, or physical sleep/wake behavior. The application was not running at the first post-login inspection, so Windows login autostart is tracked separately and is not claimed by this result.

Raw local logs and personal device identifiers are intentionally excluded from this public note.