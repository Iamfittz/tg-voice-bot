namespace VoiceBot.Domain;

public static class MessageSplitter
{
    public const int TelegramLimit = 4096;

    // Prefers whitespace boundaries so words stay whole; never cuts a surrogate pair.
    public static IReadOnlyList<string> Split(string text, int limit = TelegramLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 2);

        var parts = new List<string>();
        var rest = text.AsSpan().Trim();
        while (rest.Length > limit)
        {
            var cut = rest[..limit].LastIndexOfAny(' ', '\n');
            if (cut <= 0)
                cut = char.IsHighSurrogate(rest[limit - 1]) ? limit - 1 : limit;
            parts.Add(rest[..cut].TrimEnd().ToString());
            rest = rest[cut..].TrimStart();
        }
        if (rest.Length > 0)
            parts.Add(rest.ToString());
        return parts;
    }
}
