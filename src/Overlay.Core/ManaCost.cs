using System.Text.RegularExpressions;

namespace Overlay.Core;

public sealed record ManaToken(string Text, string First, string? Second = null, bool Phyrexian = false, bool Separator = false)
{
    public bool Hybrid => Second is not null;
}
public static class ManaCost
{
    public static ManaToken[] Parse(string cost) => Regex.Matches(cost, @"\{([^}]+)\}|//").Select(match =>
    {
        if (match.Value == "//") return new ManaToken("//", "", Separator: true);
        string text = match.Groups[1].Value.ToUpperInvariant();
        var parts = text.Split('/');
        static bool Color(string part) => part.Length == 1 && "WUBRG".Contains(part, StringComparison.Ordinal);
        static bool Half(string part) => Color(part) || part is "2" or "C";
        if (parts.Length == 3 && parts[2] == "P" && Color(parts[0]) && Color(parts[1]))
            return new ManaToken(text, parts[0], parts[1], Phyrexian: true);
        if (parts.Length == 2 && parts[1] == "P" && Color(parts[0]))
            return new ManaToken(text, parts[0], Phyrexian: true);
        if (parts.Length == 2 && Half(parts[0]) && Half(parts[1]))
            return new ManaToken(text, parts[0], parts[1]);
        return new ManaToken(text, text);
    }).ToArray();
}
