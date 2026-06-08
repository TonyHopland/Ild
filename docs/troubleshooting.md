# Troubleshooting

**Login returns 401 even with the configured password**

`ILD_PASSWORD` is only used when the bootstrap user is first created. After that, auth uses the stored PBKDF2 hash and a persisted session token. Changing `ILD_PASSWORD` after the user exists has no effect.

**The poller is not claiming work**

Confirm the WorkItem Server is configured (its own tab in the UI) with a URL, a valid WorkItem API key, and poll settings. The poller remains effectively disabled until that configuration exists.

**A work item is stuck in `Running` remotely**

The WorkItem Server reclaims stale running items when heartbeats stop arriving. Check that ILD is still tracking the item and that the poller is reaching the remote server.

**Webhook updates are not reaching ILD**

The webhook route is not an anonymous bypass. Configure the expected bearer auth and HMAC settings together; a missing or mismatched secret causes rejection.

**Preview URLs are not reachable from the host**

Only ports published by compose are reachable from the host browser. An internal preview may still be valid for AI-driven checks even when it is not externally reachable.
