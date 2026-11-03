namespace VoiceBot.Services;

public static class BotTexts
{
    public const string Welcome =
        "Привіт! Надішліть голосове, аудіо або відеоповідомлення, і я поверну текст.\n\n" +
        "Розпізнавання працює на власному сервері: аудіо не передається стороннім сервісам розпізнавання " +
        "і видаляється одразу після обробки. Текст зберігається для статистики.\n\n" +
        "/lang змінює мову запису.\n" +
        "/forget видаляє всі ваші дані.";

    public const string ChooseLanguage = "Якою мовою ви говорите в записі?";
    public const string Progress = "⏳ Розпізнаю…";
    public const string Failed = "Не вдалося обробити запис. Спробуйте ще раз.";
    public const string NoSpeech = "У записі не знайдено мови.";
    public const string TooBig = "Файл завеликий: Telegram дозволяє ботам завантажувати файли до 20 МБ.";
    public const string UserBusy = "У вас уже є записи в обробці. Дочекайтеся результату.";
    public const string QueueFull = "Зараз багато запитів. Спробуйте трохи пізніше.";
    public const string Forgotten = "Ваші дані видалено.";

    public static string TooLong(int maxSeconds) => $"Запис задовгий: максимум {maxSeconds / 60} хв.";

    public static string LanguageChanged(string languageName) =>
        $"Мова запису: {languageName}. Надішліть голосове повідомлення.";

    public static string CurrentLanguage(string languageName) =>
        $"Зараз мова запису: {languageName}. {ChooseLanguage}";
}
