# AniList metadata

AniList identifies anime and manga through its public GraphQL API. No account token is required for these catalog queries.

## Manga

Version 1.1.0 adds `comic-series` title search, exact AniList ID lookup, and HTTPS AniList manga URL lookup. Search can constrain the publication year. Results require review before application.

Manga and one-shot records supply titles, formal alternative titles, descriptions, creator credits, genres/tags, covers, date precision, and AniList/MyAnimeList identities. Chapter and volume totals remain provider statistics. The plugin does not manufacture installments, editions, ISBNs, or source releases from those counts. Light novels and anime are excluded from comic-series results. Use MangaDex when you need source chapter/release metadata.

Adult records are excluded unless Prismedia explicitly includes NSFW results. Accepted adult records mark the proposal NSFW. Exact lookup checks the returned identity and media type; an invalid URL or ID never silently becomes a title search.

## Existing anime support

Movie, video, series, season, and episode identification remains available. Structural children are only requested by the existing anime workflow. Manga lookup never invokes anime season traversal.

## Runtime limits

Each HTTP request has a 20-second deadline covering headers and body and an 8 MiB response limit, including responses without Content-Length. Searches return at most 50 candidates. The plugin reports HTTP errors without automatically retrying requests. AniList can lower its rate limit or temporarily disable access; a rate-limit response is not an empty search result.

See the [AniList media query guide](https://docs.anilist.co/guide/graphql/queries/media), [authentication guide](https://docs.anilist.co/guide/auth/), and [rate-limit documentation](https://docs.anilist.co/guide/rate-limiting).

## Validation

```sh
dotnet test tests/Prismedia.Plugin.AniList.Tests/Prismedia.Plugin.AniList.Tests.csproj
node scripts/build-plugins.mjs anilist
npm run test:manifests
npm run test:publication
```
