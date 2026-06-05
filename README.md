# Prismedia Community Plugins

Community-maintained metadata-identification plugins for [Prismedia](https://pauljoda.github.io/Prismedia/), the self-hosted media library. This repository is the public plugin **registry**. Prismedia clients fetch [`index.yml`](./index.yml) at runtime to discover what's available, then download the verified `.zip` for each plugin the user enables.

<p align="center">
  <a href="https://pauljoda.github.io/Prismedia/docs/plugins/overview">
    <img src="https://img.shields.io/badge/Read%20the%20Plugin%20Docs-2ea44f?style=for-the-badge&labelColor=24292f" alt="Read the Plugin Docs" height="40">
  </a>
</p>

> The full plugin authoring guide — input/output schemas, capability reference, runtime contract, and Prismedia client integration — lives in the docs link above. This README covers what's in the registry today and how to contribute a new plugin.

---

## Available plugins

| Plugin | Version | Runtime | Capabilities | NSFW | Description |
|---|---|---|---|---|---|
| [TMDB](./plugins/tmdb) | 1.1.0 | .NET process | `video`, `video-series`, `video-season`, `person`, `studio`, `cascade` | No | Movies, TV hierarchy, people, studios, and relationship cascade identification via The Movie Database |
| [YouTube Metadata](./plugins/youtube) | 1.1.0 | .NET process | `video`, `music-artist`, `audio-library`, `audio-track` lookup/search | No | Video metadata from YouTube URLs (InnerTube + oEmbed), plus YouTube Music artist icons, album/song square cover art, and track lists (WEB_REMIX) |
| [MusicBrainz](./plugins/musicbrainz) | 1.0.0 | .NET process | `audio-library`, `audio-track` lookup/search | No | Music metadata via MusicBrainz and Cover Art Archive |
| [AniList](./plugins/anilist) | 1.0.0 | .NET process | `video-series`, `video` lookup/search/cascade | No | Anime identification (TV, movies, OVAs) via the AniList GraphQL API |
| [MangaDex](./plugins/mangadex) | 1.1.2 | .NET process | `book` lookup/search/cascade | No | Manga and comic book identification via MangaDex |

`index.yml` is the source of truth. The table above is for humans.

---

## Repository layout

```text
.
├── index.yml                     # Registry consumed by Prismedia clients
├── package.json                  # Build tooling (root only)
├── scripts/
│   └── build-plugins.mjs         # Publishes, zips, and hashes every plugin
└── plugins/
    └── <plugin-id>/
        ├── manifest.json         # Prismedia plugin metadata + declared support
        ├── *.csproj + *.cs       # .NET process plugin host and implementation
        ├── dist/                 # Generated runtime output
        └── <id>.zip              # Distributable bundle (committed)
```

Each plugin directory is self-contained — plugins do not import from one another, and the `.zip` is the unit a client downloads and runs.

---

## Installing a plugin

Open Prismedia → **Settings → Plugins → Browse community registry**, then enable any plugin from the list. The client fetches `index.yml` from this repository, verifies the SHA-256 of the downloaded `.zip` against the registry, and runs the plugin in its sandboxed runtime.

Some plugins (TMDB) require an API key from the upstream service. Prismedia prompts for these on first use; the keys are stored in your local Prismedia keychain and passed to the plugin request as `auth` values.

For step-by-step screenshots and troubleshooting, see the [Prismedia plugin docs](https://pauljoda.github.io/Prismedia/docs/plugins/overview).

---

## Contributing a new plugin

### 1. Scaffold the directory

Mirror an existing .NET process plugin such as [`plugins/tmdb`](./plugins/tmdb):

```text
plugins/<your-plugin-id>/
├── manifest.json
├── Prismedia.Plugin.<Name>.csproj
└── Program.cs                   # plus any implementation files
```

The host should read the request JSON path from the first CLI argument and write one `{ ok, result, error }` JSON response to stdout.

### 2. Write the manifest

Required fields:

| Field | Notes |
|---|---|
| `manifestVersion` | Current registry manifest version. Use `1` unless the Prismedia docs say otherwise. |
| `apiTags` | Include `prismedia`. |
| `id` | Lowercase, no spaces. Must match the directory name. |
| `name` | Display name shown to users. |
| `version` | Semver. Bump on every change that affects the zip. |
| `runtime` | `dotnet-process`. |
| `entry` | Published assembly path under `dist/`, for example `dist/Prismedia.Plugin.Example.dll`. |
| `compat` | Prismedia/plugin API compatibility bounds. |
| `auth` | Optional list of credentials Prismedia should prompt for. Omit if the upstream API is public. |
| `supports` | Entity/action support matrix. Must match the actions handled by the plugin. |
| `isNsfw` | Set `true` if the plugin can return adult content by default. |

The full action/input/output schemas — including normalized video, series, book, person, studio, and audio result shapes — are documented in the [plugin guide](https://pauljoda.github.io/Prismedia/docs/plugins/overview).

### 3. Build & test locally

From the repo root:

```bash
npm install        # first time only
npm run build
```

`scripts/build-plugins.mjs` walks every entry in `index.yml`, publishes each .NET process plugin with `dotnet publish`, zips the plugin directory (excluding build folders and existing zips), writes `<id>.zip`, and rewrites `index.yml` with the resulting SHA-256 and manifest version. To rebuild one plugin without touching unrelated zip artifacts, pass its id, for example `node scripts/build-plugins.mjs tmdb`.

Smoke-test the published assembly with representative request JSON and any upstream API credentials required by the plugin.

### 4. Register in `index.yml`

Append a new entry. The build script overwrites `sha256`, `version`, `runtime`, compatibility, support metadata, and NSFW status from your manifest, so a placeholder checksum is fine on the first build.

```yaml
- id: my-plugin
  name: My Plugin
  version: 0.1.0
  date: '2026-04-25'
  path: plugins/my-plugin/my-plugin.zip
  sha256: PLACEHOLDER
  runtime: dotnet-process
  isNsfw: false
  description: One sentence about what this identifies
  author: Prismedia Community
  capabilities:
    supportsBatch: false
```

### 5. Open a pull request

Commit:

- `plugins/<id>/manifest.json`
- `plugins/<id>/*.csproj`
- `plugins/<id>/*.cs`
- `plugins/<id>/<id>.zip`
- `index.yml`

Commit generated `dist/` files because the plugin bundle depends on them. Include a short note in the PR describing the upstream API, any rate limits, and which test inputs you used.

---

## Versioning & releases

Releases are continuous: every merge to `main` is immediately picked up by Prismedia clients on their next registry fetch. The build script enforces that the `sha256` in `index.yml` matches the committed `.zip`, so a version bump and a rebuild are all that's needed to ship.

Bump the `version` field in your plugin's `manifest.json`, run `npm run build`, commit, and merge. Clients re-download when the version changes.

---

## Useful links

<p align="center">
  <a href="https://pauljoda.github.io/Prismedia/docs/plugins/overview">
    <img src="https://img.shields.io/badge/Plugin%20Authoring%20Guide-2ea44f?style=for-the-badge&labelColor=24292f" alt="Plugin Authoring Guide" height="36">
  </a>
  &nbsp;
  <a href="https://pauljoda.github.io/Prismedia/">
    <img src="https://img.shields.io/badge/Prismedia%20Docs-0a66c2?style=for-the-badge&labelColor=24292f" alt="Prismedia Docs" height="36">
  </a>
  &nbsp;
  <a href="https://github.com/pauljoda/prismedia/issues">
    <img src="https://img.shields.io/badge/Report%20an%20Issue-d73a49?style=for-the-badge&labelColor=24292f" alt="Report an Issue" height="36">
  </a>
</p>
