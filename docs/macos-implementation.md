# macOS implementation and compatibility

## Starting point

Read-only checks on 2026-09-10 confirmed:

- Repository root checked out on the development Mac.
- Origin: `https://github.com/osiristlg/CodexUsage.git`.
- Clean `dev/1.1` checkout and `origin/dev/1.1` at `37cfdb4`.
- Dedicated implementation branch: `codex/macos-client`.
- macOS 15.6.1, Apple Silicon, Swift 6.1.2, Command Line Tools; full Xcode and .NET SDK are absent.
- Actual logs use `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`. Initial inventory contained 62 files. A later read-only scan covered 63 files and 944 derived points with no malformed or unreadable records. Session counts change as Codex continues working.

## Stages

1. Protocol structures, CommonCrypto PBKDF2 and CryptoKit AES-GCM, plus the Windows golden vector.
2. Streaming log scanner, hourly aggregate privacy transformation, Keychain storage and network synchronization with calendar-aware scheduling.
3. SwiftUI dashboard, native settings, cached history, PNG snapshots and an ad-hoc signed app bundle.

Each stage was committed independently before integration.

## Protocol compatibility

The client preserves camel-case v1 JSON fields, including `tokens.total`, which equals input plus output. Cached input and reasoning are components, not additional total tokens. The published Windows fixture decrypts successfully; re-encrypting its exact plaintext with its fixed nonce reproduces the exact ciphertext and authentication tag. Swift's encoded payload is JSON-object equivalent to the published .NET plaintext; JSON property ordering is not part of the protocol.

PBKDF2 uses UTF-8 NFKC-normalized passphrases, a 16-byte salt, HMAC-SHA-256, 600,000 iterations and a 32-byte result. The fixture key is `94446dc0d561a436365c222aca9f1d5d2e1645816867b28054902c14dd2a9a75`, also independently reproduced with Python's standard-library PBKDF2.

AES-GCM uses 12-byte nonces and 16-byte tags. Associated data is `1\nclientId\nrequestId\nUTC timestamp`, with exactly seven fractional second digits. Incoming .NET fractional digits are preserved as text rather than round-tripped through floating-point dates. Replies must authenticate, match the client ID and have a fresh timestamp. The receiver creates a new request ID for its reply; the client does not incorrectly require it to echo the request ID.

Anonymous projects use the same first four HMAC-SHA-256 bytes in uppercase hexadecimal as Windows. Only aggregate DTOs can be passed to the exchange API. No raw-log upload path exists.

The default port is 4747. Machine names are limited to the receiver's 100 UTF-16 code units. Range replacement, full-sync markers and idempotent retry semantics are preserved. This client retries transient connection loss or timeout once using the exact same encrypted request and request ID; later refreshes replace the range again with current aggregates.

No receiver protocol changes were made. An inherited v1 limitation remains for time zones whose local midnight is not UTC-hour-aligned: the Windows-style local-midnight full range can contain a floored UTC bucket before its declared start, which the receiver rejects. Barbados and whole-hour-offset zones are unaffected. Resolving this requires agreeing on range/bucket semantics; it is not silently changed here.

## Validation and limits

Passed locally:

- Golden-vector encryption/decryption, independent PBKDF2 key, JSON schema equivalence and rejection checks.
- Scanner fixtures and read-only scan of actual Mac sessions.
- Anonymous/no-project modes, UTC-hour bounds, 2 a.m. scheduling and DST calendar-day calculations.
- Encrypted mocked exchange, retry envelope identity and failed-sync marker behavior.
- Temporary Keychain create/read/update/delete, with cleanup.
- Debug app build and release app-bundle build/ad-hoc signing.
- Authorized app launch, real-data dashboard, historical day selection, hourly project filtering, Today reset, native settings and snapshot export.
- Visual inspection of the exported PNG confirmed anonymous project labels and correctly rendered project bars. Temporary export was disabled after verification.
- Compared the live Mac dashboard and all four settings pages against eight authoritative Windows PNG references. The Mac UI now matches the Windows three-band geometry, five palettes, neon panel hierarchy, stacked model bars, project tracks, rolling line/area chart, and control-deck settings rail.
- Verified pointer-tracked hourly and 30-day tooltips, click-to-pin history selection, hover-over-pin project scoping, tooltip clamping, and the local-total/all-machines hierarchy against a live receiver response.
- Re-paired through the final app bundle after resetting its macOS privacy decision, then confirmed an app-owned Keychain credential, a successful encrypted sync, and a cold restart with unattended Keychain read and follow-up sync.

UI verification exposed and corrected missing click gestures on the charts and native progress indicators that ImageRenderer could not export. Charts now use explicit click selection and compact axis labels; project bars use SwiftUI shapes. Initial settings are persisted immediately to keep the client ID stable before pairing.

Remaining platform validation:

- Distribution signing/notarization and testing on Intel hardware or macOS 14.

The native UI carries the Windows dashboard's color palettes, visual hierarchy, hover and pinned-selection behavior into SwiftUI while retaining Mac-native windows, sheets, menus, folder panels, pointer events, and typography.

## Scanner profiling — October 2, 2026

Release-mode measurements on the local Mac, using 467 live session files (approximately 1.43 GB):

| Scan | Elapsed | Files parsed | Bytes read |
| --- | ---: | ---: | ---: |
| Original full scan | 140.90 s | 467 | Entire collection |
| Optimized cold scan | 6.00 s | 467 | 1,431,302,158 |
| Cached refresh with active sessions | 0.345 s | 3 | 36,458,292 |
| Cached refresh without changes | 0.100 s | 0 | 0 |

These are scanner measurements, not complete refresh times; aggregation, saving and encrypted receiver exchange run afterward. Logs continued growing during measurements, so event counts and bytes differ slightly between runs.

CPU sampling first showed roughly 81% of the sampled scanner thread in Unicode substring searches. Byte filtering removed that cost. A second sample then showed roughly 92% in the generic `Data.firstIndex` newline search. Bounded `memchr` replaces per-byte storage access. Timestamp formatters are reused within each file, and per-chunk autorelease pools release temporary Foundation objects during background parsing.

An actor owns the in-memory file cache. The initial implementation cached unchanged files and fully reparsed changed files. Deleted files are removed, unreadable files block publishing replacement totals, and explicit rebuilds discard cached entries. Regression checks cover append, partial final lines, token-format precedence, replacement, truncation, deletion, day rollover, forced rebuild, CRLF and read-buffer boundaries.

The project panel now contains a scrollable list within the shared chart height, preserving the dashboard's 19-point gaps as project counts increase.

## Append-tail disk I/O — October 6, 2026

The Mac already skipped unchanged session files. A live baseline of 502 files (1.49 GB) nevertheless reread 36,616,518 bytes when one session grew. Tail processing now preserves per-file model/effort/project context, both derived usage formats and the last complete-line offset. Format preference is still evaluated per file for the requested range. Unterminated trailing lines are reread from their start; oversized incomplete lines retain only discard state. No raw log bodies are retained. Two bounded 4 KB checkpoints per file validate the prefix and prior EOF before resuming; those bytes are included in diagnostics.

A release benchmark on 503 live files measured a 6.51-second cold scan, followed by a 0.155-second refresh reading 41,027 bytes across two appended tails, then a 0.073-second unchanged refresh reading zero log bytes. These are scanner timings, excluding aggregation, storage and network exchange. The live corpus grew during the runs.

Reads stop at the initial file-size snapshot. Appends after that boundary are picked up on the next refresh, allowing continuously growing sessions to retain cached state. Truncation, inode/creation-time changes, same-size modification, changed checkpoints, new calendar bounds and explicit rebuilds cause full parsing. Boundary checks assume ordinary append-only growth: an in-place edit in an unchecked middle region combined with growth can evade them. A full rebuild remains the repair path for manually rewritten logs. The cache is process-local; launch and day rollover still read the collection once.

Debug and release checks compare tail results with independent full-file scans, verify retained context and format preference, and require fewer than 20 KB read for an append to a synthetic 13 MB session. Windows sources and receiver behavior were not changed.
