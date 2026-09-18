# Kapowarr connected library

Connect an existing **Kapowarr 1.3.x** installation through Prismedia's Connections settings. Each connection has its own base URL and API key, available in Kapowarr's settings.

The plugin searches existing comic runs, reads exact issue-to-file associations, and lists every configured root folder as a provider library with its stable root ID, path, comic-series type, and Kapowarr management link. It preserves fractional and special issue labels, distinguishes Comic Vine run identities (`4050-…`), and groups combined issues under their actual file. Downloaded issue counts are not presented as file counts. Cover images and other general files are excluded.

Map a root to a dedicated **read-only** folder in Prismedia, check local access, then enable its library scan when ready. Remote file reports alone do not establish local readability. Kapowarr continues organizing the files.

## Scope

- Existing holdings only; no Comic Vine account is needed by this plugin. Kapowarr needs its own Comic Vine configuration to add new runs upstream.
- No monitoring changes, searches for releases, downloads, renames, or deletion. External-manager support supplies root choices only; Kapowarr has no matching per-run profile choice.
- Prismedia's acquisition ownership tracking currently covers movies and television. Comic roots can be scanned, but issue ownership and upgrade reconciliation are not yet offered.
- Multiple files for one issue require rendition review in Kapowarr. The plugin refuses ambiguous evidence rather than choosing one arbitrarily.
- Kapowarr exposes no persistent installation UUID. Keep the connection pointed at the same installation; individual runs are fenced with their Comic Vine identity.

## Transport

Kapowarr authenticates API requests with an `api_key` query parameter. The adapter sends it only to the configured origin, preserves reverse-proxy prefixes, refuses redirects, bounds replies to 8 MiB, and returns sanitized failures. Configure reverse-proxy access logs to omit query strings when operating Kapowarr behind one.

Validated against the released 1.3.2 API and synthetic local CBZ holdings. Authenticated Comic Vine discovery and remote acquisition are outside this validation.

Primary API reference: [released route implementation](https://github.com/Casvt/Kapowarr/blob/V1.3.2/frontend/api.py).
