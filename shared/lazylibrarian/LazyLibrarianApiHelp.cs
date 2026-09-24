using System.Text.RegularExpressions;
using Prismedia.Plugin.Integrations;

namespace Prismedia.Plugin.LazyLibrarian;

/// <summary>
/// The commands and parameters one LazyLibrarian installation declares through <c>cmd=help</c>.
/// LazyLibrarian is released continuously, so the adapter probes this list instead of pinning a
/// build. The list shows only the commands the configured key may run, so a read-only key hides
/// every write. Both the current table layout and the earlier bullet layout are read.
/// </summary>
internal sealed class LazyLibrarianApiHelp {
    #region Static Variables
    private const string RenditionParameter = "type";

    private static readonly Regex TableRow = new(@"<tr><td>([A-Za-z_]+)</td><td>(.*?)</td></tr>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ListItem = new(@"<li>([A-Za-z_]+): (.*?)</li>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Parameter = new(@"&([A-Za-z_]+)(?:=([A-Za-z0-9/]+))?",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    #endregion

    #region Variables
    /// <summary>Declared parameters by command, each with its declared value choices.</summary>
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>> commands;
    #endregion

    #region Constructors
    private LazyLibrarianApiHelp(IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>> commands) =>
        this.commands = commands;
    #endregion

    #region Actions - Decoding
    /// <summary>Reads LazyLibrarian's HTML command list.</summary>
    /// <exception cref="IntegrationFailure">The reply lists no commands.</exception>
    internal static LazyLibrarianApiHelp Parse(string html) {
        var commands = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlySet<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in TableRow.Matches(html).Concat(ListItem.Matches(html))) {
            var parameters = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
            foreach (Match parameter in Parameter.Matches(match.Groups[2].Value))
                parameters[parameter.Groups[1].Value] = parameter.Groups[2].Value
                    .Split('/', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            commands[match.Groups[1].Value] = parameters;
        }
        if (commands.Count == 0)
            throw new IntegrationFailure("LazyLibrarian's API help listed no commands, so this adapter cannot confirm which book commands it supports.");
        return new(commands);
    }
    #endregion

    #region Actions - Support
    /// <summary>
    /// Whether the installation declares a command and, for a command that takes a book format,
    /// declares that format's <c>type</c> spelling.
    /// </summary>
    internal bool Supports(LazyLibrarianCommand command, LazyLibrarianRendition? rendition = null) =>
        commands.TryGetValue(command.Name, out var parameters)
        && (!command.TakesRendition || rendition is null
            || parameters.TryGetValue(RenditionParameter, out var formats) && formats.Contains(rendition.WireType));

    /// <summary>Requires the catalog and author reads every operation depends on.</summary>
    /// <exception cref="IntegrationFailure">A core read command is not declared.</exception>
    internal void RequireReads() {
        var missing = LazyLibrarianCommand.CoreReads.Where(command => !Supports(command)).Select(command => command.Name).ToArray();
        if (missing.Length > 0)
            throw new IntegrationFailure($"LazyLibrarian's API help does not list {string.Join(" or ", missing)}. Update LazyLibrarian to read its book library.");
    }

    /// <summary>Explains why one format-scoped write is unavailable, or returns null when it is declared.</summary>
    internal string? WriteBlocker(LazyLibrarianCommand command, LazyLibrarianRendition rendition) =>
        Supports(command, rendition)
            ? null
            : $"LazyLibrarian's API help does not list {command.Name} with type={rendition.WireType}. "
                + "The configured API key may be read-only, or this LazyLibrarian build predates per-format book controls.";
    #endregion
}
