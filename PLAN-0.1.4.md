# RtlineDnsPilot 0.1.4 implementation plan

Goal: show the country each monitored site reports for the anonymous connection through the active channel, with explicit unknown results where unsupported.

1. Add optional country code/source to measurements, preserving legacy JSON compatibility and per-object independence through aggregation, history and bounded RPC projection. Verify propagation first with synthetic measurements.
2. Implement same-origin country extraction: recognized ISO code from a single X-Client-Country response header or a complete, host-matching HTTPS /cdn-cgi/trace response. Reject malformed/conflicting/foreign evidence, unsupported encodings and unrecognized codes. Do not infer country from server IP, DNS provider, language, CDN location, other sites or an account.
3. Integrate bounded optional trace reads into the existing HTTPS request: at most 4096 bytes and 500ms within the existing deadline, with cancellation. Keep availability/header latency unchanged if optional reading fails. Store no raw body or client IP.
4. Show a wrapping active-channel per-object summary with country/source/time and truthful unknown/stale/disconnected states. Retain the blue active row and existing monitoring behavior. An independent UI agent owns MainForm and an isolated smoke harness.
5. Test parser inputs, conflicting country sources, bounded reads, cancellation, per-object propagation, history/RPC compatibility and maximum frame size. Run the existing suite sequentially, then isolated UI checks and rendering; no real service/DNS mutations.
6. Bump to 0.1.4, rebuild without debug paths and produce allowlisted clean runtime/source archives. Preserve 15 public profiles, exclude machine diagnostics, audit decompressed entries, verify hashes and an isolated launch from the exact final ZIP. Keep all previous releases.

The user's original request and country clarification authorize this bounded implementation; no further design approval is required.
