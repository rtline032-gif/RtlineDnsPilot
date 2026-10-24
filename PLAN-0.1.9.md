# RtlineDnsPilot 0.1.9: DNS recovery

Outcome: recover the original DNS after a timed-out system command using actual readback and a bounded alternative Windows DNS command. Do not change real adapter DNS or Windows service state during development.

1. Reproduce the closed UDP client failure on a high loopback port; keep that client error from stopping the shared relay. Keep fatal listener failures visible.
2. During restoration, inspect the actual adapter after a failed command, accept only a verified original configuration, and otherwise try the standard Windows DNS client cmdlet for the same GUID and IP family. Do not overwrite unrelated DNS changes or infer DHCP from IP assignment.
3. Preserve durable evidence of an attempted restoration so its own partially restored ordered list can resume. Attempt restoration of both owned IP families even if one fails. Keep the journal for unconfirmed families.
4. Reject an automatic DNS source that still has effective loopback servers. Give an actionable recovery message rather than telling the user to fix netsh themselves.
5. Run focused fault-injection and loopback tests, build, appropriate regressions and isolated UI/archive checks. Produce a clean new archive; no service installation, UAC helper, real DNS writes or automatic publication.
