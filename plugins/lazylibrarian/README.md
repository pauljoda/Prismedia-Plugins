# LazyLibrarian connected library

This adapter browses an existing LazyLibrarian Book catalog and reports one ebook or single-file audiobook at a time. It does not change monitoring, queue searches, or move files. LazyLibrarian remains the owner of its library.

Create a Connection with LazyLibrarian's base URL and API key. Enter the absolute ebook and audiobook roots as LazyLibrarian reports them, then map each provider root to a Prismedia library folder that can read the same files. The roots may map to different local folders. Open a Book from Request and switch between Ebook and Audiobook to inspect or link each rendition independently. Both links lead to the same Prismedia Book work.

The adapter is pinned to the LazyLibrarian API build identified by `2b48097a`. It verifies a final file with `getFileDirect` HEAD before reporting its size. LazyLibrarian can return a ZIP for a multi-file audiobook; this adapter refuses that response because it does not enumerate the original track paths and sizes. Continue using LazyLibrarian directly for multi-file audio and searches until those scopes can be verified through its API.
