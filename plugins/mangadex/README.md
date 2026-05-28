# MangaDex

Manga and comic book identification via the MangaDex API.

## Capabilities

- `bookByURL`, `bookByName`, `bookByFragment`
- `comicByURL`, `comicByName`, `comicByFragment`
- `mangaByURL`, `mangaByName`, `mangaByFragment`

MangaDex adult ratings are requested only when Prismedia runs the plugin with NSFW mode enabled. The plugin itself is not marked NSFW.

## Auth

No MangaDex credentials are required. The plugin uses public MangaDex API endpoints and respects Prismedia's NSFW mode when requesting content ratings.
