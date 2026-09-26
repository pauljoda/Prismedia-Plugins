using System.Globalization;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.Kapowarr;

/// <summary>A Comic Vine identity family and its prefixed spelling in Prismedia's <c>comicvine</c> namespace.</summary>
internal sealed class ComicVineIdentity {
    #region Static Variables
    /// <summary>Prismedia's identity namespace for every Comic Vine identity.</summary>
    internal const string Namespace = "comicvine";

    /// <summary>A Comic Vine volume, which Kapowarr calls a run.</summary>
    internal static readonly ComicVineIdentity Series = new("4050-");

    /// <summary>One Comic Vine issue within a run.</summary>
    internal static readonly ComicVineIdentity Issue = new("4000-");
    #endregion

    #region Variables
    private readonly string prefix;
    #endregion

    #region Constructors
    private ComicVineIdentity(string prefix) => this.prefix = prefix;
    #endregion

    #region Actions - Spelling
    /// <summary>Spells a positive numeric Comic Vine ID in this family.</summary>
    /// <exception cref="IntegrationFailure">Kapowarr reported a missing or non-positive ID.</exception>
    internal string Format(int id) => id > 0
        ? prefix + id.ToString(CultureInfo.InvariantCulture)
        : throw new IntegrationFailure("Kapowarr reported a comic without a valid Comic Vine identity.");

    /// <summary>Returns this family's identity map for one numeric Comic Vine ID.</summary>
    internal IReadOnlyDictionary<string, string> Identities(int id) =>
        new Dictionary<string, string> { [Namespace] = Format(id) };

    /// <summary>Reads one prefixed identity of this family.</summary>
    /// <param name="value">The prefixed identity, such as <c>4050-1001</c>.</param>
    /// <param name="id">The positive numeric Comic Vine ID when the spelling belongs to this family.</param>
    internal bool TryParse(string? value, out int id) {
        id = 0;
        return value is not null && value.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out id)
            && id > 0;
    }
    #endregion
}
