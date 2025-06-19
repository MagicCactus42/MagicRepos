using System.Globalization;
using System.Text.RegularExpressions;

namespace MagicRepos.Core.Objects;

public sealed partial record Signature(string Name, string Email, DateTimeOffset When)
{
    /// <summary>
    /// Formats as: "Name &lt;Email&gt; unixTimestamp +0000"
    /// </summary>
    public override string ToString()
    {
        var unixSeconds = When.ToUnixTimeSeconds();
        var offset = When.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var absOffset = offset < TimeSpan.Zero ? -offset : offset;
        var offsetString = $"{sign}{absOffset.Hours:D2}{absOffset.Minutes:D2}";
        return $"{Name} <{Email}> {unixSeconds} {offsetString}";
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

    [GeneratedRegex(@"^(?<name>.+?)\s+<(?<email>[^>]+)>\s+(?<timestamp>-?\d+)\s+(?<offset>[+-]\d{4})$")]
    private static partial Regex SignaturePattern();
}
