const SHA256 = /^[a-f0-9]{64}$/;

/**
 * Keeps an unselected plugin's published index row byte-for-byte compatible with its existing zip.
 * Source manifests may already contain future work during a partial build; they must never leak into
 * the index until that plugin is selected and repackaged.
 */
export function preserveUnselectedIndexEntry(existing, pluginId, zipDigest) {
  if (!existing || typeof existing !== "object" || existing.id !== pluginId) {
    throw new Error(`Partial build requires an existing index entry for unselected plugin: ${pluginId}`);
  }
  const expectedPath = `plugins/${pluginId}/${pluginId}.zip`;
  if (existing.path !== expectedPath) {
    throw new Error(`Unselected plugin ${pluginId} has unexpected published path: ${existing.path}`);
  }
  if (typeof existing.sha256 !== "string" || !SHA256.test(existing.sha256)) {
    throw new Error(`Unselected plugin ${pluginId} has no canonical published checksum`);
  }
  if (existing.sha256 !== zipDigest) {
    throw new Error(`Unselected plugin ${pluginId} zip checksum no longer matches index.yml`);
  }
  return existing;
}
