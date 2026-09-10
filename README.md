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

It does not modify those logs or send them anywhere. Dashboard settings and the derived 30-day cache are stored under:

```text
%LOCALAPPDATA%\Codex Usage
```

Project names are derived from the working directories recorded in the session logs. They remain local unless you deliberately place an exported snapshot in a shared location. The snapshot privacy option can replace those names with generic labels.

Future multi-machine reporting will transmit only derived, aggregated usage statistics. Raw Codex session logs, prompts, responses, and other session contents will remain on the machine where they were created. Raw-log shipping is not planned: the central receiver does not need those records and will not accept them.

## Limitations

- Codex Usage reports activity recorded in local Codex session logs. It does not include activity from other machines, cloud-only sessions, or other ChatGPT surfaces.
- Historical accuracy depends on the local session logs still being present.
- Codex log formats are not a public compatibility contract and may change; unsupported record formats should be reported as issues.
- Token totals represent logged token activity and should not be treated as authoritative billing or plan-quota calculations.

## Longer-term roadmap

- A macOS client with feature parity with the Windows client
- Optional delivery of aggregated usage statistics from multiple machines
- A receiver that consolidates those aggregates into one dashboard without accepting raw logs
- Per-machine and combined usage reporting
- Authentication, transport security, deduplication, and explicit privacy controls for transmitted aggregates

The goal is a single place to understand Codex usage across all of your machines—because we can.

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

The Windows client includes local usage reporting, interactive 30-day history, configurable session-log location, themes, and snapshot export. This is the feature set intended for the v1 freeze.
