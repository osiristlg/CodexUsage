# Codex Usage 1.1.1

Version 1.1.1 adds native Mac controls for hosting the existing .NET receiver.

## Mac receiver hosting

- Initialize, configure, start, stop and health-check the Kestrel receiver from **Settings → Receiver Host**.
- Register this Mac, enable local receiver clients, or select several clients and rotate their shared passphrase together.
- Validate bind addresses, ports and explicit CIDR allowlists; unrestricted `/0` ranges are rejected.
- Apply receiver client and credential changes on the next request without restart. Listener address and port changes still require restart.
- Bundle the framework-dependent receiver payload in equipped builds while clearly requiring the .NET 10 ASP.NET Core runtime.

## Codex Usage 1.1

Version 1.1 turns Codex Usage from a useful Windows dashboard into a small multi-machine family.

## What is new

- **Mac version.** Codex Usage now has a native macOS client with the same core views in a Mac-friendly interface.
- **Private network sync.** Windows and Mac computers can contribute hourly totals to a receiver on your local network. The dashboard can show this computer, all computers, or one computer at a time.
- **One switch, every graph.** Changing the selected computer updates the daily chart, project view, and 30-day history together.
- **Privacy choices for projects.** Keep stable anonymous project IDs, share project names, or leave projects out entirely. If names are enabled later, their stable IDs let matching anonymous history gain the correct label.
- **No raw-log collection.** Network sync carries totals only. Prompts, responses, raw session logs, filenames, and working-directory paths stay on their original computer.
- **Encrypted and local.** Every enrolled computer has its own encrypted relationship with the receiver, which accepts traffic only from explicitly allowed local-network addresses.
- **Latest-image export.** The Windows app can keep one current dashboard PNG in a local or synced folder, with optional project-name hiding.
- **A more polished dashboard.** Settings, themes, hover details, historical exploration, machine selection, and long-name handling have all been tightened up.

## A note for Linux builders

There is no Linux client yet, but the shared protocol and receiver are ready for one. If this sounds like your kind of small utility, you are very welcome to port it.

Do it because you can.

## The little motto

Small utilities do not need a grand reason to exist. Sometimes making the useful thing is reason enough—because we can.
