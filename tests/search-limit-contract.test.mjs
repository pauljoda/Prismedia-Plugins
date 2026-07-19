import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const pluginSources = {
  anilist: ["plugins/anilist/Program.cs", "perPage = limit", 50],
  mangadex: ["plugins/mangadex/Program.cs", "limit={limit}", 100],
  musicbrainz: ["plugins/musicbrainz/Program.cs", "SearchLimit(request)", 100],
  openlibrary: ["plugins/openlibrary/OpenLibraryPlugin.cs", "SearchLimit(request)", 100],
  tmdb: ["plugins/tmdb/TmdbPlugin.cs", "FetchSearchResultsAsync", 100],
  youtube: ["plugins/youtube/Program.cs", "candidates.Count >= limit", 100],
};

for (const [pluginId, [behaviorPath, pagingMarker, providerMaximum]] of Object.entries(pluginSources)) {
  test(`${pluginId} honors the interactive search limit`, () => {
    const contractPaths = behaviorPath.endsWith("Program.cs")
      ? [behaviorPath]
      : [behaviorPath, `plugins/${pluginId}/PluginContracts.cs`];
    const contract = contractPaths.map((path) => readFileSync(path, "utf8")).join("\n");
    const behavior = readFileSync(behaviorPath, "utf8");

    assert.match(contract, /int Limit = 25/);
    assert.ok(
      contract.includes(`Math.Clamp(request.Query.Limit, 1, ${providerMaximum})`),
      `${pluginId} must clamp to its provider's supported maximum`,
    );
    assert.ok(
      behavior.includes(pagingMarker),
      `${pluginId} must pass the requested limit to its provider search`,
    );
  });
}
