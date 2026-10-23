# RtlineDnsPilot 0.1.8

Outcome: one Share DNS click sends eligible custom channels to the owner's fixed receiver, without setup fields or another confirmation for recipients.

1. Preserve existing eligibility and private-data filtering; empty list performs no upload.
2. Use protocol 2 and one built-in public endpoint, ignoring legacy endpoint/invite settings. Keep serialized requests, bounded transport, no ambiguous replay and clear persistent results.
3. Enable anonymous submissions only through an owner helper; keep one script-created file, narrow drive.file permission, schema validation, deduplication, fixed request counters and catalog growth limits.
4. Prepare an owner-owned Google Apps Script draft. Obtain action-time authorization for new Google access/public exposure before granting it; then verify the actual endpoint and destination file. A Drive folder URL cannot receive application POSTs.
5. Run C# functional and real-form fake-HTTP checks, existing UI/layout regressions, mocked receiver tests and clean release/archive verification. No adapter DNS or Windows service changes. Do not release with a blank/fake receiver address.
