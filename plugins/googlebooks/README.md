# Google Books

Edition-specific metadata for books and book volumes. Enable the Books API in a
Google Cloud project and configure its API key in Prismedia. The plugin reads public
volume metadata; it does not access a Google account, purchases, or private shelves.

## Selection and identity

- Search by title, optional author, and optional two-letter language code. Each
  Google volume remains a separate choice. Results show language, publisher,
  publication date, and valid ISBNs to help distinguish editions.
- ISBN lookup validates checksums and matches equivalent ISBN-10/ISBN-13 values.
  Search results always require an explicit choice, even when only one is returned.
- Selecting a volume hydrates that exact ID. Missing IDs, conflicting selectors,
  or an ISBN that does not belong to the selected volume never fall back to another
  edition or a title search. An explicit new search ignores old saved ID hints.
- Exact Google Books volume URLs are accepted; arbitrary remote URLs are not fetched.

## Metadata and limits

Proposals include title, description, identifiers, source URL, cover candidates,
author credits, categories, publisher, and the edition publication date. They do
not infer series structure, first publication of the work, or the format/edition
of an owned file. Applying a catalog proposal still requires Prismedia's metadata
review. A provider's catalog record does not prove which edition a local file holds.

Searches are bounded to 100 upstream results with at most 40 per request. HTTP reads
have a 20-second deadline and an 8 MiB limit, and redirects are rejected. Quota errors
are reported without returning credential-bearing request URLs or upstream bodies.
Google's regional catalog and API quota may change which results are available.

Official references: [API usage and keys](https://developers.google.com/books/docs/v1/using),
[volume fields](https://developers.google.com/books/docs/v1/reference/volumes),
[search parameters](https://developers.google.com/books/docs/v1/reference/volumes/list).

## Development

The project source-links the shared metadata protocol DTOs from `shared/metadata`.
The published archive includes the compiled runtime and needs no sibling checkout.
Run `dotnet test tests/Prismedia.Plugin.GoogleBooks.Tests` from the repository root,
then `node scripts/build-plugins.mjs googlebooks` to create the package.
