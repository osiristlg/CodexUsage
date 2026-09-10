# Codex Usage for macOS

A native SwiftUI and Swift Charts app for macOS 14 or later. Version 1.1.1 can report to a receiver or host the existing .NET/Kestrel receiver for a Mac-only household. The Swift app has no third-party packages.

## Build and run

Install Apple's Swift command-line tools (Swift 6.0 or newer), then from the repository root:

```sh
./macOS/build-app.sh
open "macOS/dist/Codex Usage.app"
```

The script creates an ad-hoc signed app for local development. Distribution signing and notarization are not included. Full Xcode is optional for building the app; the development machine used Swift 6.1.2 on macOS 15.6.1 with Command Line Tools only.

## Dashboard

- Today's local total, input, cached input, output and reasoning counts.
- A neon rolling 30-day line chart with floating date, total, and per-model tooltips. Click a day to pin or unpin its hourly and project views.
- Hourly stacked model bars have pointer-tracked totals; hovering an hour or history point synchronizes the project breakdown.
- Automatic refresh at 15 seconds, 30 seconds, one minute or five minutes, while the app is running.
- **Rebuild 30 days** rescans the logs and queues a full network reconciliation, retained across restart until a successful sync.
- The same Night City, Neon Sunset, Toxic Rain, Ion Storm, and Redline District palettes as the Windows dashboard.
- Optional PNG snapshots every 5, 15, 30 or 60 minutes, with project names hidden by default. Export atomically replaces `codex-usage-latest.png`.

Snapshots are generated on the next refresh after their interval elapses. Refresh and synchronization resume after sleep while the app is open; no launch agent or background service is installed. The app intentionally rescans the local logs on refresh for correctness, including older session files that receive new events. Large log collections may take longer.

## Settings and privacy

The initial session directory is the current user's `~/.codex/sessions`; choose another directory in Settings. Logs are read without modification. The scanner recognizes `session_meta`, `turn_context`, `event_msg/token_count` and `token_usage_record`; per file it prefers token-count events and uses legacy usage records only when no token-count events exist in the scanned range, matching the Windows parser.

Only derived usage points and settings are cached under `~/Library/Application Support/Codex Usage/mac-*.json`. The cache includes local project labels and event times, but no paths, session IDs, prompts, responses or raw records. An unreadable file or directory prevents publishing partial replacement totals. Malformed records and in-flight final lines are skipped and revisited on refresh.

Reporting starts disabled. In **Settings → Network**:

1. Copy the client ID and register it on your receiver as described in [LAN reporting](../docs/lan-reporting.md).
2. Enter the receiver origin, normally `http://<LAN-address>:4747`, and your machine name.
3. Enter the shared passphrase and use **Pair / Test encrypted connection**.
4. Select the project-detail level, enable reporting, and save.

Pairing derives the key locally, verifies an encrypted query, and stores only the derived 32-byte key in macOS Keychain. Keychain entries are scoped to the receiver and client ID and are device-local. The passphrase is cleared from the form and never persisted or transmitted. Anonymous project IDs are the default; **Project names** is an explicit opt-in. No project breakdown merges rows into `All projects`.

Signed distribution builds keep the derived network key in macOS Keychain. Local `build-app.sh` bundles are ad-hoc signed, which gives every rebuild a new Keychain identity. To prevent macOS login-Keychain prompts during development, those bundles store only the derived 32-byte network key in `~/Library/Application Support/Codex Usage/mac-network-credentials.json`, restricted to the current user with mode `0600`. The receiver passphrase is still cleared from the form and is never persisted. Rebuilding an ad-hoc app preserves this development credential without asking for the Mac login password.

### Hosting the receiver on a Mac

Open **Settings → Receiver Host**. The page initializes the receiver data directory, validates the bind address, port and explicit allowed CIDRs, registers this Mac as a client, starts or stops the local receiver, and shows live health and enabled-client counts. It can select several local clients and rotate their shared passphrase in one operation. Enter that same passphrase in every selected client app afterward.

Hosting requires the .NET 10 ASP.NET Core runtime. The release app bundles the framework-dependent receiver DLL and its dependencies; it does not bundle the runtime. GUI launches locate standard Intel and Apple Silicon .NET installations without relying on the shell `PATH`. Credentials stay in `~/Library/Application Support/Codex Usage/Receiver/receiver-settings.json`; salts and derived keys are never displayed or exposed by an administration endpoint. Client enablement and credential changes take effect without restarting the receiver. Bind-address and port changes require a restart because Kestrel owns the listening socket.

`build-app.sh` publishes and embeds the receiver when a .NET SDK is installed. A release pipeline can instead set `CODEX_USAGE_RECEIVER_PAYLOAD` to an existing framework-dependent `dotnet publish` directory. A build without either remains a fully functional reporting client and explains that its receiver payload is unavailable.

Normal refreshes replace the current and previous UTC hours. The first successful refresh after 2 a.m. local time replaces 30 local calendar days, as does a rebuild. A failed attempt does not advance the successful full-sync marker. Network and privacy configuration changes also queue full reconciliation. Combined totals are labeled as the last successful sync and are hidden when they refer to a previous local day or reporting is disabled.

## Verification

The checks are a standalone Swift executable, so they also run on Command Line Tools installations without XCTest:

```sh
swift run --package-path macOS UsageCoreChecks
swift run --package-path macOS UsageCoreChecks --scan-local
swift run --package-path macOS UsageCoreChecks --keychain
```

`--scan-local` reads the local sessions and prints only aggregate diagnostic counts. `--keychain` exercises a unique temporary credential and deletes it afterward. Run checks in debug mode (the default), where preconditions remain enabled.

Coverage includes the published Windows AES-GCM vector and PBKDF2 key, Swift JSON schema equivalence, NFKC normalization, .NET tick precision, wrong keys, tampered headers, unsupported versions, scanner format precedence, anonymous labels, hourly grouping, 2 a.m. scheduling, DST transitions, encrypted mocked replies and idempotent transport retry. No live receiver credentials are required.

See [implementation and compatibility notes](../docs/macos-implementation.md) for validation results and remaining manual checks.
