# Separate Windows 7 build

The user requested Windows 7 compatibility and supplied a missing GetSystemTimePreciseAsFileTime error. Preserve the normal Windows 10/11 target and prepare a distinct Windows 7 SP1 x64 build.

- Target the legacy variant at net6.0-windows with Microsoft .NET Desktop Runtime 6.0.36 and LatestPatch roll-forward. Build using a modern SDK/compiler and the official 6.0.428 targeting packs; do not patch Windows or use unofficial runtimes.
- Backport missing BCL helpers without changing DNS frame validation, exact-read/EOF behavior, enum/ASCII checks, cancellation or disposed-object errors.
- Enforce recursive required JSON fields on .NET 6. Preserve null/case/unknown-property semantics and avoid extra work on model graphs with no required fields.
- On Windows 7 use HTTP/1.1 and TLS 1.2 with standard certificate verification. Preserve pinned DNS/DoH and public-only monitoring.
- Preserve GUID/owner/journal protections and use a bounded native netsh retry instead of unavailable Windows 8+ PowerShell modules. Preserve first-instance control-pipe exclusivity and ACLs.
- Isolate legacy update asset selection, runtime validation and cached launch pointers from the normal variant.
- Verify actual .NET 6 build/tests plus normal .NET 10 regression checks, package checksums/privacy and isolated UI renders. State environment-specific DPAPI failures and the absence of Windows 7 acceptance separately.
- Package the 15 public profiles with explicit Windows 7 SP1 x64/.NET 6 Desktop prerequisites. No personal configuration, network snapshots or logs are distributable.

Rebuild on Windows 10/11 with .NET SDK 10: `powershell -File build.ps1 -Windows7 -LegacySdkDirectory C:\Toolchains\dotnet6 -Destination artifacts-win7`. The optional toolchain directory is the verified official SDK ZIP extracted locally. Building on Windows 7 with the SDK 6 compiler is not supported by this source's C# language version. Running the legacy application requires only the .NET 6 Desktop Runtime, not the SDK.
