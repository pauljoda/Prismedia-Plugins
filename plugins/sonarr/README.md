# Sonarr connection

Connect an existing Sonarr 4.x instance through its API v3. Configure the application base URL (including a reverse-proxy prefix when used) and API key. Add independent Connections for different instances.

## Implemented

- Report confirmed removal only after the exact holding is missing and a healthy complete catalog also excludes it; outages remain unverified failures.

- Browse/search existing series by title or metadata ID.
- List every configured Sonarr root folder as a provider library with its stable root ID, path, series type, and Sonarr management link.
- Resolve exact TVDB or TMDB series identities and adopt an existing series without changing its path, profile, or monitoring.
- Add a missing series unmonitored with no automatic search, then bind every initially selected episode to Sonarr's canonical episode ID and observed numbering.
- Expose bounded overview, genres, runtime, certification, and public poster/fanart remote URLs for read-only holding presentation. Authenticated, local-only, and credential-bearing cover URLs are omitted.
- Inspect exact final file associations and read existing profile/root-folder choices.
- Preserve combined-episode associations and specials in Sonarr.
- Check the selected holding's metadata identity before trusting a reused remote item ID.
- Read current configuration for exact pinned episode IDs and numbering.
- Change only those episode monitoring flags when the parent series is already monitored.
- Request one explicit `EpisodeSearch` and observe its exact command ID and original queue timestamp.

Prismedia must reserve the finite episode scope and persist each dispatch fence before a mutation. Creation supports only that explicit initial episode set; expanding the scope of an active series request is unsupported. The adapter never enables series monitoring or changes its series-wide quality profile. Searches can target selected episodes while the parent series is unmonitored; they do not change any monitoring flags. File presence is reported by the external application; a local mapping and verified access are required before Prismedia can play those bytes.

Initial creation uses `POST series` with `monitored=false`, `monitor=none`, and both automatic-search flags disabled. It never issues `EpisodeSearch`; the host dispatches that separately after its durable request fence. Monitoring uses `PUT episode/monitor` with exact episode IDs and one explicit flag. Search uses `POST command` with `EpisodeSearch` and exact episode IDs. The adapter preserves unrelated episodes, existing parent settings, paths, and files. It never moves or deletes files or executes full-series searches. Changed identities, coordinates, reviewed folders, profiles, or relevant monitoring values require fresh review.

Sonarr provides no idempotency key or atomic conditional mutation for these endpoints. Writes are never retried by the adapter, and a timeout or invalid reply after dispatch is uncertain. The host can reconcile desired flags by reading but cannot infer a lost search's command ID from similar history. Command completion is independent of downloads or local byte availability. Missing history or command-ID reuse stays unknown.

Neither supported API reports a persistent installation UUID. The adapter reports that absence explicitly, and Prismedia scopes item and library IDs to the Connection. Profile and folder choices retain their external IDs. An unavailable server produces an error, never an empty successful library.

## Boundaries

Control reads have an 8 MiB response limit and 20-second per-request deadline. Redirects are rejected and API keys are sent only to the configured origin. The upstream library-list endpoint is not paginated; oversized responses fail explicitly. Returned pages use stable numeric-ID ordering and query-bound cursors. A library may change between pages.

## Verification

`dotnet test tests/Prismedia.Plugin.Arr.Tests` covers both adapters. Live reads were validated against Radarr 6.1.1.10360 and Sonarr 4.0.17.2952. Broader minor-version support depends on the same API v3 resource shapes.

Official APIs: [Radarr](https://radarr.video/docs/api/), [Sonarr](https://sonarr.tv/docs/api/).

The exact identity and unmonitored creation flow follows the supported version's [series lookup](https://github.com/Sonarr/Sonarr/blob/v4.0.17.2952/src/Sonarr.Api.V3/Series/SeriesLookupController.cs), [series controller](https://github.com/Sonarr/Sonarr/blob/v4.0.17.2952/src/Sonarr.Api.V3/Series/SeriesController.cs), and [add-series service](https://github.com/Sonarr/Sonarr/blob/v4.0.17.2952/src/NzbDrone.Core/Tv/AddSeriesService.cs). The finite monitoring and search payloads follow its [episode controller](https://github.com/Sonarr/Sonarr/blob/v4.0.17.2952/src/Sonarr.Api.V3/Episodes/EpisodeController.cs) and [episode search command](https://github.com/Sonarr/Sonarr/blob/v4.0.17.2952/src/NzbDrone.Core/IndexerSearch/EpisodeSearchCommand.cs).

## Ownership handoff inspection

The read-only `inspect-managed-release` operation checks the exact holding and finite monitoring scope before and after activity inspection. It requires a complete empty download queue, including unknown items, and terminal command states. Download-client health problems also block release: an unavailable client can make an empty queue incomplete. Health and queue APIs are observations, not a remote lock or a guarantee against a newly started download. Keep the selected scope unmonitored and avoid starting work directly during the host's handoff.
