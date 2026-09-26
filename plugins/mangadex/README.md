# MangaDex

Serialized manga identification via the MangaDex API.

## Capabilities

- `comic-series` lookup and search, including title metadata and structural children
- `comic-volume` lookup through stable MangaDex title-and-volume identities
- `comic-installment` lookup by MangaDex chapter identity or URL

MangaDex chapters are independently published releases. The plugin therefore maps titles,
volumes, and chapters to Prismedia's serialized comic hierarchy instead of prose-book
chapters. A volume is emitted only when MangaDex exposes a grouping; uncollected chapters
remain direct children of their series.

MangaDex adult ratings are requested only when Prismedia runs the plugin with NSFW mode enabled. The plugin itself is not marked NSFW.

## Auth

No MangaDex credentials are required. The plugin uses public MangaDex API endpoints and respects Prismedia's NSFW mode when requesting content ratings.

## Exact chapter designations

Requires Prismedia 3.8.0 or newer. Chapter labels such as `12.5` travel in typed
position entries independently from their catalog order. Stored exact labels take
precedence over integer fallback positions. An explicit label or release ID that
cannot be found stays unresolved rather than selecting a neighboring chapter.
