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

test("tmdb declares kind-scoped provider identity URLs", () => {
  const manifest = JSON.parse(readFileSync(join(pluginsRoot, "tmdb", "manifest.json"), "utf8"));
  const identityUrls = Object.fromEntries(
    manifest.supports.map((support) => [support.entityKind, support.identityUrls ?? []]),
  );

  assert.deepEqual(identityUrls, {
    movie: [{
      identityNamespace: "tmdb",
      valuePattern: "{id}",
      urlTemplate: "https://www.themoviedb.org/movie/{id}",
    }],
    video: [
      {
        identityNamespace: "tmdbepisode",
        valuePattern: "{seriesId}:{seasonNumber}:{episodeNumber}",
        urlTemplate: "https://www.themoviedb.org/tv/{seriesId}/season/{seasonNumber}/episode/{episodeNumber}",
      },
      {
        identityNamespace: "tmdb",
        valuePattern: "{id}",
        urlTemplate: "https://www.themoviedb.org/movie/{id}",
      },
    ],
    "video-series": [{
      identityNamespace: "tmdb",
      valuePattern: "{id}",
      urlTemplate: "https://www.themoviedb.org/tv/{id}",
    }],
    "video-season": [{
      identityNamespace: "tmdbseason",
      valuePattern: "{seriesId}:{seasonNumber}",
      urlTemplate: "https://www.themoviedb.org/tv/{seriesId}/season/{seasonNumber}",
    }],
    person: [{
      identityNamespace: "tmdb",
      valuePattern: "{id}",
      urlTemplate: "https://www.themoviedb.org/person/{id}",
    }],
    studio: [{
      identityNamespace: "tmdb",
      valuePattern: "{id}",
      urlTemplate: "https://www.themoviedb.org/company/{id}",
    }],
  });
});

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

test("manifest-v2 accepts a safe identity URL format", () => {
  const manifest = validManifest({
    supports: [{
      entityKind: "video-season",
      actions: ["lookup-id"],
      identityNamespaces: ["tmdbseason"],
      identityUrls: [{
        identityNamespace: "tmdbseason",
        valuePattern: "{seriesId}:{seasonNumber}",
        urlTemplate: "https://www.themoviedb.org/tv/{seriesId}/season/{seasonNumber}",
      }],
    }],
  });

  assert.equal(validateManifest(manifest, "invalid"), manifest);
});

test("manifest-v2 permits identity URLs to be omitted or empty", () => {
  const omitted = validManifest();
  const empty = validManifest({
    supports: [{
      entityKind: "book",
      actions: ["lookup-id"],
      identityNamespaces: ["openlibrary"],
      identityUrls: [],
    }],
  });

  assert.equal(validateManifest(omitted, "invalid"), omitted);
  assert.equal(validateManifest(empty, "invalid"), empty);
});

test("manifest-v2 requires identity URL namespaces to be canonical and declared by the same support", () => {
  for (const identityNamespace of ["TMDB", "imdb"]) {
    const manifest = validManifest({
      supports: [{
        entityKind: "movie",
        actions: ["lookup-id"],
        identityNamespaces: ["tmdb"],
        identityUrls: [{
          identityNamespace,
          valuePattern: "{id}",
          urlTemplate: "https://www.themoviedb.org/movie/{id}",
        }],
      }],
    });

    assert.throws(() => validateManifest(manifest, "invalid"), /identity URL namespace/);
  }
});

test("manifest-v2 rejects duplicate identity URL formats for one namespace", () => {
  const format = {
    identityNamespace: "tmdb",
    valuePattern: "{id}",
    urlTemplate: "https://www.themoviedb.org/movie/{id}",
  };
  const manifest = validManifest({
    supports: [{
      entityKind: "movie",
      actions: ["lookup-id"],
      identityNamespaces: ["tmdb"],
      identityUrls: [format, { ...format }],
    }],
  });

  assert.throws(() => validateManifest(manifest, "invalid"), /duplicate identity URL namespace/);
});

test("manifest-v2 rejects ambiguous or malformed identity value patterns", () => {
  for (const valuePattern of ["id", "{}", "{ id }", "{id}:{id}", "{id}{other}", "{id"]) {
    const manifest = validManifest({
      supports: [{
        entityKind: "movie",
        actions: ["lookup-id"],
        identityNamespaces: ["tmdb"],
        identityUrls: [{
          identityNamespace: "tmdb",
          valuePattern,
          urlTemplate: "https://www.themoviedb.org/movie/{id}",
        }],
      }],
    });

    assert.throws(() => validateManifest(manifest, "invalid"), /identity URL value pattern/);
  }
});

test("manifest-v2 requires safe HTTPS templates whose placeholders come from the value pattern", () => {
  for (const urlTemplate of [
    "http://www.themoviedb.org/movie/{id}",
    "javascript:alert({id})",
    "https://user@www.themoviedb.org/movie/{id}",
    "https://www.themoviedb.org/movie/{missing}",
    "https://www.themoviedb.org/movie/static",
    "https://www.themoviedb.org/movie/{id",
  ]) {
    const manifest = validManifest({
      supports: [{
        entityKind: "movie",
        actions: ["lookup-id"],
        identityNamespaces: ["tmdb"],
        identityUrls: [{
          identityNamespace: "tmdb",
          valuePattern: "{id}",
          urlTemplate,
        }],
      }],
    });

    assert.throws(() => validateManifest(manifest, "invalid"), /identity URL template/);
  }
});

test("manifest-v2 identity URLs preserve every captured identity component", () => {
  const omitted = validManifest({
    supports: [{
      entityKind: "video-season",
      actions: ["lookup-id"],
      identityNamespaces: ["tmdbseason"],
      identityUrls: [{
        identityNamespace: "tmdbseason",
        valuePattern: "{seriesId}:{seasonNumber}",
        urlTemplate: "https://www.themoviedb.org/tv/{seriesId}",
      }],
    }],
  });
  assert.throws(() => validateManifest(omitted, "invalid"), /omits an identity placeholder/);

  const repeated = validManifest({
    supports: [{
      entityKind: "video-season",
      actions: ["lookup-id"],
      identityNamespaces: ["tmdbseason"],
      identityUrls: [{
        identityNamespace: "tmdbseason",
        valuePattern: "{seriesId}:{seasonNumber}",
        urlTemplate: "https://www.themoviedb.org/tv/{seriesId}/season/{seasonNumber}?series={seriesId}",
      }],
    }],
  });
  assert.equal(validateManifest(repeated, "invalid"), repeated);
});
