# LAN aggregate reporting

## What crosses the network

Each client scans its own Codex session logs and sends only derived hourly rows containing:

- UTC hour
- model label
- input, cached-input, output, reasoning, and response counts
- either an anonymous project ID, no project breakdown, or a project name when explicitly selected
- the user-configured machine name

Prompts, responses, raw log records, filenames, session IDs, repository paths, and file contents are never added to the payload. The receiver exposes only pairing-salt, health, and encrypted aggregate-exchange endpoints.

## Pairing a dashboard

1. On the receiver machine, initialize the configuration:

   ```powershell
   dotnet run --project .\src\CodexUsage.Receiver -- --init
   ```

2. Open `%LOCALAPPDATA%\Codex Usage Receiver\receiver-settings.json`. Set `BindAddress` to the receiver's LAN address and replace `AllowedSubnets` with the LAN CIDR ranges that may connect. Do not use an unrestricted range.
3. In the reporting dashboard, open **Settings → Network** and copy its client ID.
4. On the receiver, register it:

   ```powershell
   dotnet run --project .\src\CodexUsage.Receiver -- --add-client <client-id> "Office desktop"
   ```

5. Enter a long, unique shared passphrase when prompted. Enter the same value in the dashboard; it is used locally to derive the encryption key and is never transmitted.
6. Set the dashboard receiver address, choose the project-detail level, click **Test connection**, then enable reporting and save.
7. Start the receiver with `dotnet run --project .\src\CodexUsage.Receiver` or arrange for its published executable to run at sign-in.

Repeat registration for each dashboard. Each client receives its own random salt and derived key.

## Synchronization behaviour

- Normal refreshes replace the current and previous UTC-hour buckets, making late log updates converge.
- The first successful refresh after 2 a.m. local time replaces the last 30 local days. Failed attempts retry on subsequent refreshes.
- **Rebuild 30 days** forces a full replacement.
- Request IDs make retries idempotent. The receiver replaces the entire declared range within one database transaction.
- The receiver returns today's combined total and per-machine totals in the encrypted response. The dashboard shows the combined total below its local total.

## Security model

Protocol version 1 derives a 256-bit key from the shared passphrase using PBKDF2-HMAC-SHA-256 with a per-client 128-bit random salt and 600,000 iterations. Payloads and replies use AES-256-GCM with a fresh 96-bit nonce and a 128-bit authentication tag. Protocol version, client ID, request ID, and timestamp are authenticated as associated data. Envelopes older than 15 minutes are rejected, accepted request IDs are deduplicated, request size and row count are capped, and source addresses must match an explicit CIDR allowlist.

On Windows, each dashboard stores only a DPAPI-protected derived key. The receiver stores per-client derived keys in its local settings file, so protect the receiver account and data directory. The network still reveals connection metadata such as IP addresses, timing, and payload sizes. This design does not use TLS or certificates; use it only on a trusted private LAN, not across the public internet or an untrusted Wi-Fi network.

## Receiver data and backup

By default, receiver files are under `%LOCALAPPDATA%\Codex Usage Receiver`:

- `receiver-settings.json` contains the bind policy and client credentials.
- `usage.db` contains aggregate rows and replay records.

Set the `CODEX_USAGE_RECEIVER_DATA` environment variable before any receiver command to relocate both files. Stop the receiver before copying or restoring the directory.
