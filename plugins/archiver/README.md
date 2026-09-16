# The Archiver executor API

Connect multiple Archiver installations through independent Prismedia Connections.
This adapter targets the **proposed executor API v1**, not legacy Archiver routes.
An existing installation must implement this interface before it can be connected.

Configure the application's base URL and a dedicated integration bearer token, then
enable discovery and transfer execution and test the connection. Use Prismedia's
**Requests → Import from URL** to inspect a URL and explicitly select a publication.

## Supported profile

- URL inspection; search is not advertised.
- One EPUB/PDF book or CBZ comic through the negotiated `single-publication` profile.
- Persistent installation identity and same-operation recovery after uncertain POSTs.
- Authoritative job snapshots, remote cancellation, immutable paged manifests.
- Authenticated same-origin artifact retrieval with exact sizes and SHA-256 evidence.
- Renewable retention and independently retryable import receipts.

Prismedia owns final placement and import. The Archiver owns source credentials,
extraction, packaging, and its spool. API tokens are never forwarded to another origin.
The adapter rejects redirects for control requests and checks installation identity
before every operation. Archive bytes travel directly to the host's bounded staging
transport; they never pass through the plugin's JSON output.

The Prismedia repository includes `apps/backend/tools/Prismedia.IntegrationSimulator`
with synthetic EPUB/CBZ files, durable restart behavior, and response-loss injection.
Its HTTP schema is independent of Prismedia's database and domain entities. It can
validate this adapter while The Archiver is rebuilt separately.

```sh
dotnet test tests/Prismedia.Plugin.Archiver.Tests
node scripts/build-plugins.mjs archiver
```
