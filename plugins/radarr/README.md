# Radarr connection

Connect an existing Radarr 6.x instance through its API v3. Configure the application base URL (including a reverse-proxy prefix when used) and API key. Add independent Connections for different instances.

## Implemented

- Browse/search existing movies by title or metadata ID.
- Expose bounded overview, genres, runtime, certification, and public poster/fanart remote URLs for read-only holding presentation. Authenticated, local-only, and credential-bearing cover URLs are omitted.
- Inspect exact final file associations and read existing profile/root-folder choices.
- Check the selected holding's metadata identity before trusting a reused remote item ID.
- Observe monitoring, profile, and a previously acknowledged search command for one exact movie.
- Apply explicit monitoring/profile fields using Radarr's partial movie-editor API, preserving omitted fields and file paths.
- Submit one `MoviesSearch` command for the selected movie without changing monitoring.
- Look up an exact TMDB identity and distinguish an existing holding from a metadata candidate.
- Add a missing movie with the selected profile and reviewed root, initially unmonitored with search and collection monitoring disabled. Existing holdings retain their configuration.

The host must persist ownership and dispatch intent before invoking a mutation. The operation ID is host correlation; Radarr does not provide an idempotency guarantee for it. The adapter never retries a write. A lost or malformed write response remains uncertain; it must not be treated as a definite rejection or automatically resubmitted. Read-only reconciliation can observe configuration and an already known command reference.

Command references include both the numeric ID and original queue timestamp. Missing history, reused IDs, changed command coverage, and unknown execution outcomes remain unverified. A completed search can find no releases and never establishes file availability. A local mapping and verified access are still required before Prismedia can play reported files.

The adapter checks pinned TMDB/other identities, the reviewed managed path, and relevant configuration before writing. These checks are not an upstream compare-and-swap transaction: concurrent changes in another client can still race. File organization, import, and deletion remain outside this adapter's operations.

Creation uses a separate `ensure-managed` operation. It first resolves the exact identity, validates the chosen profile/root, and adds only a missing movie. New movies use Radarr's released-availability policy; monitoring and search require later explicit controls. A fresh movie read verifies the acknowledged identity, root, and initial settings. If a response is lost, use `lookup-managed` to reconcile; an absent lookup result does not prove that an earlier POST failed. The host must retain an uncertain creation instead of automatically repeating it.

Neither supported API reports a persistent installation UUID. The adapter reports that absence explicitly, and Prismedia scopes item IDs to the Connection. Profile and folder choices retain their external IDs. An unavailable server produces an error, never an empty successful library.

## Boundaries

Control reads have an 8 MiB response limit and 20-second per-request deadline. Redirects are rejected and API keys are sent only to the configured origin. The upstream library-list endpoint is not paginated; oversized responses fail explicitly. Returned pages use stable numeric-ID ordering and query-bound cursors. A library may change between pages.

## Verification

`dotnet test tests/Prismedia.Plugin.Arr.Tests` covers both adapters, exact mutation scopes, preserve semantics, lost responses, and command identity reuse. Live reads were validated against Radarr 6.1.1.10360 and Sonarr 4.0.17.2952. Mutation semantics were checked against the [versioned movie editor](https://github.com/Radarr/Radarr/blob/v6.1.1.10360/src/Radarr.Api.V3/Movies/MovieEditorController.cs) and command APIs. Broader minor-version support depends on the same API v3 resource shapes.

Official APIs: [Radarr](https://radarr.video/docs/api/), [Sonarr](https://sonarr.tv/docs/api/).

## Ownership handoff inspection

The read-only `inspect-managed-release` operation checks the exact holding and finite monitoring scope before and after activity inspection. It requires a complete empty download queue, including unknown items, and terminal command states. Download-client health problems also block release: an unavailable client can make an empty queue incomplete. Health and queue APIs are observations, not a remote lock or a guarantee against a newly started download. Keep the selected scope unmonitored and avoid starting work directly during the host's handoff.
