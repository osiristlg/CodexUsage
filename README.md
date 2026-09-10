# Codex Usage

Codex Usage is a lightweight desktop dashboard for understanding how Codex is being used on your machine. It reads the session logs written by Codex, turns the token records into useful daily and historical views, and keeps everything local.

The current client is built for Windows with .NET and Windows Forms.

## What it shows

- Today's total token usage, split into input, cached input, output, and reasoning tokens
- Hourly usage grouped by model
- Usage grouped by project
- An interactive rolling 30-day history
- Historical hourly and project breakdowns
- Automatic refresh, with a configurable interval
- Optional encrypted, aggregate-only reporting across machines on the same LAN
- Several built-in visual themes

Hover over the charts for more detail. Select a day in the 30-day chart to inspect it, and use **Rebuild 30 days** when you want to rescan the local history immediately.

## Snapshot export

The dashboard can periodically export its current view as a PNG. This is useful for a personal dashboard, a synced folder, or another display that watches a single image file.

From **Settings → Snapshot Export**, you can:

- Enable or disable capture
- Choose a local or synced destination folder
- Capture every 5, 15, 30, or 60 minutes
- Hide project names for privacy

The exporter atomically replaces `codex-usage-latest.png`; it does not accumulate an archive of old images. Capture preferences persist across application restarts.

No upload service or webhook is included. If the selected folder happens to be synchronized by another application, that synchronization remains entirely under the user's control.

## Data and privacy

Codex Usage currently reads session data from the signed-in user's standard Codex session directory:

```text
%USERPROFILE%\.codex\sessions
```

The session directory is configurable from **Settings → Codex Data → Choose session folder**. Its initial value is derived at runtime from the current user's operating-system home directory, so the application does not contain a hard-coded username or machine-specific path. The selected location persists across application restarts.

It does not modify those logs. Unless you explicitly enable LAN reporting, it does not send usage data anywhere. Dashboard settings and the derived 30-day cache are stored under:

```text
%LOCALAPPDATA%\Codex Usage
```

Project names are derived from the working directories recorded in the session logs. They remain local unless you deliberately place an exported snapshot in a shared location or choose **Project names** for LAN reporting. Snapshot export can replace names with generic labels. LAN reporting defaults to stable anonymous project IDs and can instead omit the project breakdown entirely.

LAN reporting transmits only hourly token counts grouped by model and, depending on your privacy setting, project. Raw Codex logs, prompts, responses, filenames, and paths remain on the machine where they were created. The receiver has no endpoint for raw-log upload. See [LAN reporting](docs/lan-reporting.md) for setup and security details.

## LAN reporting

One machine runs the receiver and each dashboard reports its own derived aggregates to it. The receiver defaults to loopback-only access, so LAN access must be deliberately enabled in its generated settings.

Initialize and configure the receiver:

```powershell
dotnet run --project .\src\CodexUsage.Receiver -- --init
dotnet run --project .\src\CodexUsage.Receiver -- --add-client <client-id> "<machine name>"
dotnet run --project .\src\CodexUsage.Receiver
```

The client ID is shown in **Settings → Network**. The add-client command asks for a shared passphrase without displaying it. Enter the same passphrase in that machine's dashboard, set the receiver's `http://host:port` address, test the encrypted exchange, and enable reporting.

The receiver writes its settings and SQLite database under `%LOCALAPPDATA%\Codex Usage Receiver`. Set `CODEX_USAGE_RECEIVER_DATA` to use a different data directory, which is useful for service accounts or isolated testing. Edit `receiver-settings.json` to choose the listening address and explicit allowed CIDR ranges before starting it on the LAN.

## Limitations

- Codex Usage reports activity recorded in local Codex session logs. It does not include activity from other machines, cloud-only sessions, or other ChatGPT surfaces.
- Historical accuracy depends on the local session logs still being present.
- Codex log formats are not a public compatibility contract and may change; unsupported record formats should be reported as issues.
- Token totals represent logged token activity and should not be treated as authoritative billing or plan-quota calculations.

## Requirements

- Windows 10 or later
- A compatible .NET Desktop Runtime for framework-dependent builds
- Local Codex session logs

## Run from source

Install the .NET SDK used by the project, then run:

```powershell
dotnet run --project .\CodexUsageDashboard.csproj
```

To create a release build:

```powershell
dotnet publish -c Release -o .\dist --self-contained false
```

Launch `Codex Usage.exe` from the resulting `dist` directory.

## Current status

The Windows client includes local reporting, interactive 30-day history, configurable session-log location, themes, snapshot export, and optional encrypted aggregate reporting to a LAN receiver.
