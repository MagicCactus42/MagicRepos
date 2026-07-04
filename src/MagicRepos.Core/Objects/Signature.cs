using System.Globalization;
using System.Text.RegularExpressions;

namespace MagicRepos.Core.Objects;

public sealed partial record Signature
{
    public string Name { get; }
    public string Email { get; }
    public DateTimeOffset When { get; }

    public Signature(string Name, string Email, DateTimeOffset When)
    {
        ArgumentNullException.ThrowIfNull(Name);
        ArgumentNullException.ThrowIfNull(Email);

        // Reject anything that would prevent a byte-exact round-trip through ToString/Parse.
        // A name/email that cannot round-trip would produce a commit that can never be read
        // back (or, with an embedded newline, would inject fake commit headers).
        if (!IsValidName(Name))
            throw new ArgumentException(
                "Name must be non-empty, without surrounding whitespace or the characters '<', '>', CR, or LF.",
                nameof(Name));
        if (!IsValidEmail(Email))
            throw new ArgumentException(
                "Email must be non-empty, without whitespace or the characters '<', '>', CR, or LF.",
                nameof(Email));

        this.Name = Name;
        this.Email = Email;
        this.When = When;
    }

    private static bool IsValidName(string name)
    {
        if (name.Length == 0 || name != name.Trim())
            return false;
        return !name.Any(c => c is '<' or '>' or '\n' or '\r');
    }

    private static bool IsValidEmail(string email)
    {
        if (email.Length == 0)
            return false;
        return !email.Any(c => c is '<' or '>' or '\n' or '\r' || char.IsWhiteSpace(c));
    }

    /// <summary>
    /// Formats as: "Name &lt;Email&gt; unixTimestamp +0000" (culture-invariant).
    /// </summary>
    public override string ToString()
    {
        long unixSeconds = When.ToUnixTimeSeconds();
        TimeSpan offset = When.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        TimeSpan absOffset = offset < TimeSpan.Zero ? -offset : offset;
        string offsetString = string.Create(CultureInfo.InvariantCulture,
            $"{sign}{absOffset.Hours:D2}{absOffset.Minutes:D2}");
        return string.Create(CultureInfo.InvariantCulture,
            $"{Name} <{Email}> {unixSeconds} {offsetString}");
    }

    /// <summary>
    /// Parses a signature line in the format: "Name &lt;Email&gt; unixTimestamp +0000"
    /// </summary>
    public static Signature Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var match = SignaturePattern().Match(line);
        if (!match.Success)
            throw new FormatException($"Invalid signature format: '{line}'");

        var name = match.Groups["name"].Value;
        var email = match.Groups["email"].Value;
        var timestamp = long.Parse(match.Groups["timestamp"].Value, CultureInfo.InvariantCulture);
        var offsetStr = match.Groups["offset"].Value;

        var offsetSign = offsetStr[0] == '-' ? -1 : 1;
        var offsetHours = int.Parse(offsetStr[1..3], CultureInfo.InvariantCulture);
        var offsetMinutes = int.Parse(offsetStr[3..5], CultureInfo.InvariantCulture);
        var offset = new TimeSpan(offsetSign * offsetHours, offsetSign * offsetMinutes, 0);

        var when = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToOffset(offset);

        return new Signature(name, email, when);
    }

    // Record equality on DateTimeOffset compares instants only; two signatures with the same
    // instant but different offsets would compare equal yet serialize to different bytes (and
    // therefore different commit ids). Compare the offset explicitly so equal signatures are
    // substitutable.
    public bool Equals(Signature? other) =>
        other is not null
        && Name == other.Name
        && Email == other.Email
        && When.EqualsExact(other.When);

    public override int GetHashCode() =>
        HashCode.Combine(Name, Email, When.ToUnixTimeSeconds(), When.Offset);

    // Name may contain spaces, so the name group is lazy and the separators are exactly one
    // space each; combined with constructor validation this makes ToString/Parse exact inverses.
    [GeneratedRegex(@"^(?<name>[^<>\n\r]+?) <(?<email>[^<>\s]+)> (?<timestamp>-?\d+) (?<offset>[+-]\d{4})$")]
    private static partial Regex SignaturePattern();
}
