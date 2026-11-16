# NetPilot Implementation Plan

Goal: deliver a locally built Windows application and source archive implementing DESIGN.md.

Architecture: WinForms client, protected Windows service and restricted named pipe, loopback DNS relay, independent profile probes and measured sticky selection. .NET 10 Desktop is already installed on this workstation. No external packages, downloads, installs or real DNS mutations during development.

1. Define shared profile, probe, statistics, UI and RPC contracts. Write behavioral tests for validation and selection, then implement the selection engine and atomic JSON storage.
2. Independently implement DNS wire/transport, per-profile HTTPS probes and bounded UDP/TCP loopback relay. Verify against local controlled servers and cancellation/malformed-response cases.
3. Independently implement the Russian client UI, standalone monitoring, profile CRUD/import/export, tray and service commands. Render a local UI smoke image.
4. Implement adapter snapshots and exact DNS-only netsh plans, write-ahead journal and rollback/conflict behavior with an injectable backend. Implement service host and ACL-restricted RPC. Implement explicit UAC installation/uninstallation into protected locations; do not run these during development.
5. Integrate and run build, protocol/selection/journal tests and UI smoke render. Probe supplied channels read-only and preserve results with routing limitations. Inspect the rendered interface and fix concrete defects.
6. Package executable/runtime files, source and Russian instructions in outputs, including test evidence and the explicit boundary that privileged runtime acceptance has not occurred.

No additional design or execution-choice approval is required: the user selected the local intermediary and instructed implementation after planning. Do not widen the scope to VPN/proxy management or external publication.
