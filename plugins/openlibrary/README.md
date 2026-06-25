# Open Library Plugin

Open Library provides Prismedia book metadata for prose books and book-series shaped libraries. It is intentionally Open Library-first because Goodreads no longer issues public API keys, Hardcover currently requires an account token, and Google Books is useful but less consistent for series structure.

## What It Identifies

- `book`: individual works such as `A Game of Thrones`, or synthetic series roots when a series subject is selected. Folder-backed books are treated as likely series containers, so a search for a known volume can prefer the containing series candidate. Series children are emitted as individual `book` proposals so scanned single-file novels can hydrate in place.
- `book-volume`: structural book-volume entities, with `volumeNumber` and `sortOrder` when Open Library series subjects expose an ordered set.
- `person`: author metadata, including bio, life dates, Open Library author photos, official links, and remote IDs such as Wikidata/Goodreads/VIAF when Open Library has them.

## Metadata Coverage

The plugin fills titles, descriptions, external IDs, Open Library URLs, ISBNs, cover images, subjects/tags, series tags, page counts, publishers, publish dates, author credits, and author relationship proposals. Series support uses Open Library subjects like `series:A Song of Ice and Fire`, then queries that subject in publication order to build child book proposals and resolve child books/book-volumes from parent context.

## Provider Notes

Open Library asks API clients to use structured API endpoints rather than scraping HTML, cache where possible, and keep interactive usage low volume. The plugin sends a Prismedia user agent and uses a cross-process throttle so identify cascades remain polite.

## Test Cases

The primary live smoke case used during development was George R. R. Martin's `A Game of Thrones` (`OL257943W`) and the `A Song of Ice and Fire` series subject. The unit tests cover series candidate generation, work hydration, edition selection, series positioning, author relationship hydration, and ID parsing.
