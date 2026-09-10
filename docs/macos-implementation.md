# macOS implementation and compatibility

## Starting point

Read-only checks on 2026-09-10 confirmed:

- Git root: `/Users/steve.porter/codex/CodexUsage`.
- Origin: `https://github.com/osiristlg/CodexUsage.git`.
- Clean `dev/1.1` checkout and `origin/dev/1.1` at `37cfdb4`.
- Dedicated implementation branch: `codex/macos-client`.
- macOS 15.6.1, Apple Silicon, Swift 6.1.2, Command Line Tools; full Xcode and .NET SDK are absent.
- Actual logs use `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`. Initial inventory contained 62 files. A later read-only scan covered 63 files and 944 derived points with no malformed or unreadable records. Session counts change as Codex continues working.

## Stages

1. Protocol structures, CommonCrypto PBKDF2 and CryptoKit AES-GCM, plus the Windows golden vector.
2. Streaming log scanner, hourly aggregate privacy transformation, Keychain storage and network synchronization with calendar-aware scheduling.
3. SwiftUI dashboard, native settings, cached history, PNG snapshots and an ad-hoc signed app bundle.

Each stage is committed independently. No push or merge is part of this work.

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

UI verification exposed and corrected missing click gestures on the charts and native progress indicators that ImageRenderer could not export. Charts now use explicit click selection and compact axis labels; project bars use SwiftUI shapes. Initial settings are persisted immediately to keep the client ID stable before pairing.

Pending manual validation:

- Pairing and exchanging with an actual configured .NET receiver; no live receiver endpoint or passphrase was supplied and the local .NET SDK is absent.
- Distribution signing/notarization and testing on Intel hardware or macOS 14.

The native UI provides the Windows dashboard's core views and snapshot controls, with native light/dark/system styling rather than its custom Windows themes. The receiver and Windows source files remain unchanged.
