# DNS recovery after a manual DHCP change

Goal: recover the independently owned IP family when the other family conflicts, and let the explicit automatic-DNS action accept a verified automatic setting already selected in Windows.

Spec: DESIGN.md. Preserve GUID/owner validation, pending-writer protection, exact ordinary Restore, disabled bindings and journals for unresolved conflicts. Development performs no real DNS writes, service operations or UAC actions.

- [ ] Reproduce an IPv6 conflict leaving owned IPv4 loopback unchanged in TransactionTests.
- [ ] Reproduce explicit automatic reset against a reopened relay journal after Windows has already selected DHCP. Ordinary Restore must still preserve that changed setting and report a conflict.
- [ ] Move conflict validation inside the per-family recovery loop. Completed families remain recorded; unresolved families keep their original settings.
- [ ] Allow only the explicit automatic reset to complete matching-adapter journal families already verified as DHCP without loopback. Keep active-relay, corruption, unknown-writer, wrong-adapter and arbitrary static-change guards.
- [ ] Route the stopped-service automatic-DNS helper directly through that operation, keeping normal Restore unchanged.
- [ ] Run focused recovery/automatic-reset tests, full local suite and Release build. Prepare clean 0.1.11 archives with 15 public profiles and verify hashes/isolated UI launches.

The reported Windows 11 computer is not the development host. Local tests do not prove recovery on that adapter.

Publication requested by the user: preserve a verified manual DHCP choice during stopped-service recovery, archive the original snapshot before retiring ownership, and recheck state after archiving. This allows the existing 0.1.10 updater to use the new elevated installer without requiring a manual GUI replacement. Add regression coverage for the stopped-owner, archive, unchanged-DNS, independent-family, foreign-static and pending-writer boundaries. Rebuild and verify the public archives, publish matching source and the 0.1.11 release, then verify that the 0.1.10 downloader selects and validates it. Real service/UAC/DNS acceptance remains on the user's computer.
