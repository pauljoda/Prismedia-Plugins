import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import test from "node:test";

import { validateManifest } from "../scripts/manifest-contract.mjs";

const pluginsRoot = resolve("plugins");
const expectedContracts = {
  anilist: {
    "video-series": { identities: ["anilist"], fields: ["seriesTitle", "year"] },
    "video-season": { identities: ["anilistseason"], fields: [] },
    video: { identities: ["anilistepisode", "anilist"], fields: ["title", "year"] },
  },
  mangadex: {
    book: { identities: ["mangadex"], fields: ["title", "creator", "year"] },
    "book-volume": { identities: ["mangadexvolume"], fields: [] },
    "book-chapter": { identities: ["mangadexchapter"], fields: [] },
  },
  musicbrainz: {
    "music-artist": { identities: ["musicbrainzartist", "musicbrainz"], fields: ["title", "country", "startYear"] },
    "audio-library": { identities: ["musicbrainzrelease", "musicbrainzreleasegroup", "musicbrainz"], fields: ["title", "artist", "year"] },
    "audio-track": { identities: ["musicbrainzrecording", "musicbrainz"], fields: ["title", "artist", "album", "year"] },
  },
  openlibrary: {
    book: { identities: ["openlibrary", "openlibrarywork", "openlibraryedition", "isbn", "isbn10", "isbn13"], fields: ["title", "author", "year", "seriesTitle"] },
    "book-volume": { identities: ["openlibrary", "openlibrarywork", "openlibraryedition", "isbn", "isbn10", "isbn13"], fields: ["title", "author", "year", "seriesTitle"] },
    person: { identities: ["openlibrary", "openlibraryauthor"], fields: ["title", "birthYear"] },
  },
  tmdb: {
    movie: { identities: ["tmdb"], fields: ["title", "year"] },
    video: { identities: ["tmdbepisode", "tmdb"], fields: ["title", "year"] },
    "video-series": { identities: ["tmdb"], fields: ["seriesTitle", "year"] },
    "video-season": { identities: ["tmdbseason"], fields: [] },
    person: { identities: ["tmdb"], fields: ["title"] },
    studio: { identities: ["tmdb"], fields: ["title"] },
  },
  youtube: {
    video: { identities: ["youtube"], fields: ["title", "channel"] },
    "music-artist": { identities: ["youtubechannel", "youtube"], fields: ["title"] },
    "audio-library": { identities: ["youtubealbum", "youtube"], fields: ["title", "artist", "year"] },
    "audio-track": { identities: ["youtube"], fields: ["title", "artist", "album"] },
  },
};

for (const pluginId of readdirSync(pluginsRoot).sort()) {
  const manifestPath = join(pluginsRoot, pluginId, "manifest.json");
  const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));

  test(`${pluginId} publishes a valid manifest-v2 contract`, () => {
    assert.equal(validateManifest(manifest, pluginId), manifest);
    assert.deepEqual(
      Object.fromEntries(manifest.supports.map((support) => [support.entityKind, {
        identities: support.identityNamespaces,
        fields: support.search?.fields.map((field) => field.key) ?? [],
      }])),
      expectedContracts[pluginId],
    );
  });
}

function validManifest(overrides = {}) {
  return {
    manifestVersion: 2,
    apiTags: ["prismedia"],
    id: "invalid",
    name: "Validation fixture",
    version: "1.0.0",
    date: "2026-07-09",
    runtime: "dotnet-process",
    entry: "dist/Validation.dll",
    compat: {
      pluginApiMin: "2.0.0",
      pluginApiMax: null,
      prismediaMin: "2.0.1",
      prismediaMax: null,
    },
    auth: [],
    isNsfw: false,
    supports: [{
      entityKind: "book",
      actions: ["lookup-id"],
      identityNamespaces: ["openlibrary"],
    }],
    ...overrides,
  };
}

test("manifest-v2 rejects legacy cascade actions", () => {
  const invalid = validManifest({
    supports: [{
      entityKind: "book",
      actions: ["lookup-id", "cascade"],
      identityNamespaces: ["openlibrary"],
    }],
  });

  assert.throws(() => validateManifest(invalid, "invalid"), /unknown action/);
});

test("manifest-v2 rejects missing required top-level fields", () => {
  const invalid = validManifest();
  delete invalid.isNsfw;
  assert.throws(() => validateManifest(invalid, "invalid"), /boolean isNsfw/);
});

test("manifest-v2 requires protocol 2 compatibility", () => {
  const invalid = validManifest({
    compat: {
      pluginApiMin: "1.0.0",
      pluginApiMax: null,
      prismediaMin: "2.0.1",
      prismediaMax: null,
    },
  });
  assert.throws(() => validateManifest(invalid, "invalid"), /pluginApiMin >= 2.0.0/);
});

test("manifest-v2 rejects a missing manifest object", () => {
  assert.throws(() => validateManifest(null, "missing"), /requires an object at root/);
});
