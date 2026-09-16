namespace Prismedia.Plugin.GoogleBooks;

/// <summary>Checksum-validated ISBN with an equivalent ISBN-13 matching value.</summary>
internal sealed record BookIsbn(string Value, string Canonical13) {
    public static BookIsbn? Parse(string? raw) {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 64) return null;
        var value = new string(raw.Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray());
        if (value.Length == 13 && value.All(char.IsAsciiDigit)) {
            if (!value.StartsWith("978", StringComparison.Ordinal) && !value.StartsWith("979", StringComparison.Ordinal)) return null;
            return Check13(value[..12]) == value[12] ? new(value, value) : null;
        }
        if (value.Length != 10 || !value[..9].All(char.IsAsciiDigit) || !(char.IsAsciiDigit(value[9]) || value[9] == 'X')) return null;
        var sum = value.Select((c, index) => (10 - index) * (c == 'X' ? 10 : c - '0')).Sum();
        if (sum % 11 != 0) return null;
        var prefix = "978" + value[..9];
        return new(value, prefix + Check13(prefix));
    }
    private static char Check13(string prefix) => (char)('0' + (10 - prefix.Select((c, index) => (c - '0') * (index % 2 == 0 ? 1 : 3)).Sum() % 10) % 10);
}
