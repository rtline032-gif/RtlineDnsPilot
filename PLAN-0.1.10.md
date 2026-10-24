# Application update implementation plan

Goal: one explicit Update Application action downloads and validates a newer official release, updates an existing service using its existing UAC installer, and restarts the GUI without losing user channels/history.

Architecture: verified side-by-side application versions in the current user's LocalAppData, an atomic launch pointer, and a separate unprivileged completion worker. Keep the old runtime for recovery. The only elevated action remains the existing service installer with its fixed Program Files destination, owner checks, DNS restoration and observation-mode result.

Constraints:
- First installation of this update-capable version is manual; old distributed binaries cannot gain a new button without replacement.
- Updates are manual only, from rtline032-gif/RtlineDnsPilot GitHub Releases. No tokens, telemetry, startup download or arbitrary update URL.
- Select a numerically newer published version with one matching Windows x64 ZIP and a SHA-256 digest from GitHub. Published early releases remain identifiable. Reject downgrades and drafts.
- Bound metadata/download/uncompressed bytes and redirects. Validate HTTPS hosts, digest, ZIP paths, file allowlist, checksums, PE/runtime metadata and expected version before launching anything.
- User data stays in existing NetPilot LocalAppData/ProgramData. Do not copy, reset or upload config, profiles edited in the application, statistics or DNS journals during GUI replacement.
- Existing service update finishes in observation mode. A restoration/installer failure prevents committing the new GUI launch pointer.
- Save an old runtime snapshot; worker waits for the exact parent PID/start-time to exit, revalidates staged files, invokes the new service installer only if a service was installed, commits the pointer and launches the new window. On failed startup/ack, restore the previous pointer and launch the old snapshot. Do not downgrade the service after a GUI launch failure.
- On subsequent normal GUI starts, a previously approved, intact newer pointer may launch its local cached version. Helpers, service, probe and GUI smoke modes never follow this pointer.
- Development tests use fixtures, fake HTTP, isolated directories and injected process/service hosts. No real adapter-DNS writes, service changes, UAC actions or publication.

Implementation:
1. ApplicationUpdateClient.cs and Tests/ApplicationUpdateClientTests.cs: metadata selection, bounded verified download and strict extraction; return StagedApplicationUpdate.
2. ApplicationUpdateLauncher.cs and Tests/ApplicationUpdateLauncherTests.cs: staged-file verification, old snapshot, completion worker, launch pointer, startup ack and rollback, with OS/process/service calls behind a small injected host.
3. Program.cs / MainForm.cs / UiBackend.cs: common Update Application button, version/progress text, serialized close, unsaved settings protection, worker/ack routing and unchanged data path. Keep Update Service distinct.
4. ServiceControl.cs: reuse package validation with an explicit expected future version for download preflight; installation itself still defaults to the installer's own compiled version.
5. Run focused behavior/failure tests, existing networking/recovery regressions and full suite. Keep the known unrelated DPAPI profile limitation separate. Build and verify clean 0.1.10 runtime/source archives and isolated GUI rendering.
