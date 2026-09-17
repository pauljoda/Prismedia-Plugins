# Suwayomi Sources

Connects Prismedia to the installed manga sources on one Suwayomi Server `2.3.2243` instance.

Plugin release `1.0.1` keeps catalog identity stable across Suwayomi database ID rebuilds while continuing to revalidate the exact pinned chapter before acquisition.

The catalog starts with installed sources. Choose one source to browse popular manga or search that source, then open a manga to choose an exact chapter. Prismedia can request that single chapter, observe Suwayomi's queue, and import its CBZ after the chapter download is complete.

## Safety and scope

- Only comic installments are offered.
- Requests enqueue exactly one pinned chapter. They never install extensions, add manga to the Suwayomi library, request a series or collection, delete remote downloads, or change reading progress.
- Existing Suwayomi library manga use cached chapter records. Refresh them in Suwayomi so its own automatic-download rules remain under Suwayomi's control.
- A newly discovered manga outside the Suwayomi library is refreshed only when it has no cached chapter list.
- CBZ delivery always uses `markAsRead=false`.
- Suwayomi does not provide Prismedia's durable executor identity, manifest, hash, retention, cancellation, or receipt guarantees. This plugin therefore exposes finite source preparation and direct delivery only.
- The reviewed queue behavior coalesces repeated requests for the same chapter ID. Prismedia still observes the exact pinned chapter before retrying an accepted request whose first response was lost.

## Connection

Use the Suwayomi server root URL, including any reverse-proxy path prefix. HTTP and HTTPS are accepted for private-network deployments. Configure either a bearer token or username/password authentication when the server requires it.

This release intentionally accepts only server version `2.3.2243`; later server versions require a compatibility review before the plugin widens its range.
