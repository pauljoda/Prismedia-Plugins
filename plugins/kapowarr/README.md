# Kapowarr connected library

Connect an existing **Kapowarr 1.3.x** installation through Prismedia's Connections settings. Each connection has its own base URL and API key, available in Kapowarr's settings.

The plugin searches existing comic runs, lists every issue including those without a file, reads exact issue-to-file associations, and lists every configured root folder as a provider library with its stable root ID, path, comic-series type, and Kapowarr management link. It preserves fractional and special issue labels, shows each issue's current monitoring flag, distinguishes Comic Vine run identities (`4050-…`) and issue identities (`4000-…`), and groups combined issues under their actual file. Downloaded issue counts are not presented as file counts. Cover images and other general files are excluded.

Map a root to a dedicated **read-only** folder in Prismedia, check local access, then enable its library scan when ready. Remote file reports alone do not establish local readability. Kapowarr continues organizing the files.

## Scope

- Existing holdings work without a Comic Vine account in Prismedia. Kapowarr needs its own Comic Vine API key to look up and add a new run.
- A linked, locally matched issue can change its own monitoring or queue one immediate search. These controls never expand to sibling issues, and Kapowarr has no matching per-run profile choice.
- Kapowarr searches an issue only when both the issue and its run are monitored and the issue has no file, so an issue counts as monitored only inside a monitored run. Turning an issue's monitoring on also sets its run's own monitoring flag. That write carries no monitoring scheme, so other issues keep their flags and new-issue monitoring stays as it is. The adapter refuses instead when monitoring the run would widen Kapowarr's searches: the run monitors new issues, or other issues without files are already monitored.
- Search is offered only when Kapowarr's automatic issue search would actually run. A request for an unmonitored run or issue, or for an issue that already has a file, is rejected with that reason instead of queueing a task that does nothing.
- Comic Vine catalog search returns run identities for review. Read-only lookup resolves one exact issue in an existing run, or confirms an unadded run by its Comic Vine ID. It fails if an existing issue is absent or its label changed.
- A reviewed creation intent can add one missing run to its mapped root with run monitoring, issue monitoring, and automatic search off. The adapter then re-reads the run and pins the requested Comic Vine issue before the host can apply issue controls. A lost add response remains uncertain until exact identity lookup reconciles it.
- Search acknowledgement means Kapowarr accepted a task. Its task IDs have no durable completion history, so Prismedia keeps the outcome unverified when the task disappears. A search does not promise a download or readable file.
- The current Prismedia comic review page starts from an existing connected run. A new-run review entry point still needs to be wired in the host UI. The adapter does not download directly, rename, or delete.
- An issue with several files cannot be matched to one exact file. The run stays readable: that issue keeps its entry, but its files, and any other issue sharing them, are left out of the file list. Controls, lookups, and requests that target those issues fail with the reason until Kapowarr keeps one file per issue. The plugin never chooses one file arbitrarily.
- A run that Kapowarr reports as missing is reported to Prismedia as removed only when Kapowarr's complete run list also omits it. A 404 alone, such as from a misrouted proxy, is an ordinary failure.
- A definite refusal from Kapowarr (HTTP 4xx) before any change is reported as a rejected action with its reason. Timeouts, server errors, and unconfirmed results stay uncertain and are never repeated automatically.
- Kapowarr exposes no persistent installation UUID. Keep the connection pointed at the same installation; individual runs are fenced with their Comic Vine identity.

## Transport

Kapowarr authenticates API requests with an `api_key` query parameter. The adapter sends it only to the configured origin, preserves reverse-proxy prefixes, refuses redirects, bounds replies to 8 MiB, and returns sanitized failures. Configure reverse-proxy access logs to omit query strings when operating Kapowarr behind one.

Validated against an isolated Kapowarr 1.3.2 instance with local CBZ fixtures. Exact issue monitoring and task acknowledgement were exercised; the original monitoring flags were restored. The isolated instance has no Comic Vine API key, so new-run lookup and creation have contract tests but no real upstream acceptance yet.

Primary API reference: [released route implementation](https://github.com/Casvt/Kapowarr/blob/V1.3.2/frontend/api.py).
