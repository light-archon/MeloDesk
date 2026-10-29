using System.IO;

namespace MeloDesk;

public static class AppPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusiGram");
    public static string LegacyRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeloDesk");
    private static readonly Lazy<string> TelegramLocation = new(() => ResolveTelegramRoot(Root, LegacyRoot));
    public static string TelegramRoot => TelegramLocation.Value;

    public static string ResolveTelegramRoot(string currentRoot, string legacyRoot)
    {
        var marker = Path.Combine(currentRoot, "telegram-session-location.txt");
        if (File.Exists(marker)) return ReadLocation();
        var legacy = Path.Combine(legacyRoot, "tdlib");
        var location = File.Exists(Path.Combine(legacy, "database-key.dat"))
            && File.Exists(Path.Combine(legacy, "database", "td.binlog")) ? "legacy" : "current";
        Directory.CreateDirectory(currentRoot);
        var temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, location);
            try { File.Move(temporary, marker, overwrite: false); }
            catch (IOException) when (File.Exists(marker)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return ReadLocation();

        string ReadLocation() => File.ReadAllText(marker).Trim() switch
        {
            "legacy" => Path.Combine(legacyRoot, "tdlib"),
            "current" => Path.Combine(currentRoot, "tdlib"),
            _ => throw new InvalidDataException("Не удалось прочитать путь сохранённой сессии Telegram. База не изменена.")
        };
    }
}
