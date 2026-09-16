# Sonarr connection

Connect an existing Sonarr 4.x instance through its API v3. Configure the application base URL (including a reverse-proxy prefix when used) and API key. Add independent Connections for different instances.

## Implemented

- Browse/search existing series by title or metadata ID.
- Inspect exact final file associations and read existing profile/root-folder choices.
- Preserve combined-episode associations and specials in Sonarr.
- Check the selected holding's metadata identity before trusting a reused remote item ID.

This package currently performs GET requests only. It does not add holdings, change monitoring, issue searches, import files, or delete anything. File presence is reported by the external application; a local mapping and verified access are required before Prismedia can play those bytes.

Neither supported API reports a persistent installation UUID. The adapter reports that absence explicitly, and Prismedia scopes item IDs to the Connection. Profile and folder choices retain their external IDs. An unavailable server produces an error, never an empty successful library.

## Boundaries

Control reads have an 8 MiB response limit and 20-second per-request deadline. Redirects are rejected and API keys are sent only to the configured origin. The upstream library-list endpoint is not paginated; oversized responses fail explicitly. Returned pages use stable numeric-ID ordering and query-bound cursors. A library may change between pages.

## Verification

`dotnet test tests/Prismedia.Plugin.Arr.Tests` covers both adapters. Live reads were validated against Radarr 6.1.1.10360 and Sonarr 4.0.17.2952. Broader minor-version support depends on the same API v3 resource shapes.

Official APIs: [Radarr](https://radarr.video/docs/api/), [Sonarr](https://sonarr.tv/docs/api/).
