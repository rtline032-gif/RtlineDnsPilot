# Code signing

## Current status — 2026-10-24

RtlineDnsPilot 0.1.11 is **unsigned**. No code-signing certificate or SignPath Foundation approval has been obtained. The project is preparing a free open-source application to [SignPath Foundation](https://signpath.org/). A prepared application, a published repository or a passing build does not mean that the Foundation has accepted the project.

This page will be updated if signing is approved and a signed release is actually available. Until then, download pages must continue to describe builds as unsigned. A SHA-256 checksum verifies file integrity against a trusted published checksum; it does not establish publisher trust or replace Authenticode signing.

## Release ownership and build

The project maintainer and intended release approver is [rtline032-gif](https://github.com/rtline032-gif). Source code, build definitions and changes are public in this repository. The Windows build workflow compiles the source and runs local verification before producing an unsigned artifact. The workflow has read-only repository permissions and does not hold a signing key.

## Proposed policy if approved

Only RtlineDnsPilot releases built from the corresponding published source and reviewed by the maintainer are intended to be submitted for signing. Each signing request requires release approval. Contributors do not receive a private signing key. The final integration must meet the Foundation's current requirements, including account protection, artifact provenance and approval controls.

SignPath Foundation certificates are issued in the Foundation's name rather than as a personal certificate for this project's owner. Signing cannot guarantee immediate Microsoft SmartScreen reputation or approval by every antivirus product.

See the [Foundation terms](https://signpath.org/terms.html) and the application's [privacy description](PRIVACY.md).
