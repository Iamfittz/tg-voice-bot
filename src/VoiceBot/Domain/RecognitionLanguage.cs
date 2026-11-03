namespace VoiceBot.Domain;

public sealed class UserSettings
{
    public long Id { get; set; }
    public string Language { get; set; } = RecognitionLanguage.Auto;
}

public sealed record LanguageOption(string Code, string ButtonLabel, string Name);

public static class RecognitionLanguage
{
    public const string Auto = "auto";

    public static IReadOnlyList<LanguageOption> Options { get; } =
    [
        new("uk", "🇺🇦 Українська", "українська"),
        new("ru", "Русский", "російська"),
        new("en", "🇬🇧 English", "англійська"),
        new(Auto, "🌐 Автовизначення", "автовизначення")
    ];

    public static bool IsSupported(string code) => Options.Any(o => o.Code == code);

    public static string Name(string code) => Options.First(o => o.Code == code).Name;

    // The language of the Telegram app says nothing about the spoken language: with "ru" set,
    // Whisper translates Ukrainian speech into Russian. Automatic detection is unreliable on short clips.
    // So only the user's own choice overrides the configured default.
    public static string Resolve(string? saved, string fallback) => saved ?? fallback;
}
