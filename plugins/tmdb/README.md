# TMDB Plugin

TMDB is a Prismedia community `dotnet-process` plugin. It identifies movies, TV series, seasons, episodes, people, and studios through The Movie Database API.

## Runtime Contract

Prismedia launches the published .NET assembly declared in `manifest.json` and passes the path to a request JSON file as the first CLI argument. The plugin writes a single JSON response to stdout:

```json
{ "ok": true, "result": { } }
```

Errors are returned as:

```json
{ "ok": false, "error": "message" }
```

The TMDB API key is read from `auth.apiKey`, `auth.TMDB_API_KEY`, or the `TMDB_API_KEY` environment variable.

## Code Layout

| File | Responsibility |
|---|---|
| `Program.cs` | Minimal process entry point |
| `TmdbPluginHost.cs` | CLI request/response serialization and top-level error handling |
| `TmdbAuth.cs` | API key lookup |
| `TmdbPlugin.cs` | Entity-kind dispatch, direct-id/url lookup, and search orchestration |
| `TmdbApiClient.cs` | TMDB HTTP calls |
| `TmdbProposalMapper.cs` | Converts TMDB responses into Prismedia metadata proposals |
| `TmdbMetadataHelpers.cs` | URL parsing, title scoring, position/context parsing, and shared metadata helpers |
| `PluginContracts.cs` | Prismedia request/response records |
| `TmdbContracts.cs` | TMDB API response records |

## Build

From the repository root:

```bash
node scripts/build-plugins.mjs tmdb
```

For a fast compile check while editing:

```bash
dotnet build plugins/tmdb/Prismedia.Plugin.Tmdb.csproj --no-restore
```

The registry bundle is `plugins/tmdb/tmdb.zip`; rebuild it when source, manifest, or version changes need to ship.
