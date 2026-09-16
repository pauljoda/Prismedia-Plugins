# OPDS Catalogs

Connect OPDS 1.2 Atom and OPDS 2 JSON catalogs to Prismedia's catalog browser. Requires Prismedia 3.8 or newer.

Install the plugin, then add a **Connection** using the catalog root URL. Preserve a trailing slash when the catalog uses directory-relative links. Each connection has its own optional Basic username/password or bearer token. Do not configure both authentication methods.

The adapter supports:

- Navigation and paginated publication browsing, including grouped JSON catalogs.
- Search when the root advertises a supported OpenSearch description or search template.
- EPUB, PDF, Mobipocket, and Amazon ebook offers; CBZ and CBR comic offers.
- Explicit distinctions between direct full publications, loans, purchases, samples, and external workflows.
- Revalidation of the exact selected publication and offer before returning server-only retrieval instructions.

Navigation, direct retrieval, redirects, and authentication are restricted to the configured HTTP origin. Cross-origin acquisition links remain external offers. DRM/license, indirect acquisition, lending, checkout, and subscription workflows are not executable downloads. PDF publications without comic-specific evidence are classified as books.

Catalog documents are limited to 4 MiB after decompression. XML external entities and DTDs are disabled. The adapter does not infer a persistent server installation identity from a feed ID; Prismedia scopes source items to the connection.

## Development

```sh
dotnet test tests/Prismedia.Plugin.Opds.Tests
node scripts/build-plugins.mjs opds
```

The shared integration contracts live in `shared/integrations`. Metadata identify v2 remains a separate protocol. Integration requests carry a protocol version, invocation ID, typed operation, instance context, and input. Responses must echo the protocol and invocation ID. Secrets and resolved HTTP delivery instructions are server-only; browser discovery uses protected host tokens.

Reference specifications: [OPDS 1.2](https://specs.opds.io/opds-1.2), [OPDS 2](https://specs.opds.io/opds-2.0).
