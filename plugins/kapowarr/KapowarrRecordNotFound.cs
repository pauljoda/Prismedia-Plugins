namespace Prismedia.Plugin.Kapowarr;

/// <summary>
/// Kapowarr answered a read with 404 and its own error envelope. This is read evidence only: a
/// complete catalog read must also omit the record before the adapter may report confirmed removal.
/// </summary>
internal sealed class KapowarrRecordNotFound() : Exception("Kapowarr reported that the requested record does not exist.");
