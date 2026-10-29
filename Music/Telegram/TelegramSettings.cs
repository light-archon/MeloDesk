using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MeloDesk.Telegram;

public sealed record TelegramCredentials(int ApiId, string ApiHash);
public static class TelegramSettings
{
    private static string PathValue => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusiGram", "telegram-credentials.dat");
    public static TelegramCredentials? Load()
    {
        var path = File.Exists(PathValue) ? PathValue : Path.Combine(AppPaths.LegacyRoot, "telegram-credentials.dat");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<TelegramCredentials>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }
    public static TelegramCredentials? LoadConfigured()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("MUSIGRAM_API_ID") ?? Environment.GetEnvironmentVariable("MELODESK_API_ID"), out var id)
            && (Environment.GetEnvironmentVariable("MUSIGRAM_API_HASH") ?? Environment.GetEnvironmentVariable("MELODESK_API_HASH")) is { } hash)
        {
            var environment = new TelegramCredentials(id, hash);
            if (IsValid(environment)) return environment;
        }
        var appFile = Path.Combine(AppContext.BaseDirectory, "telegram-app.local.json");
        if (File.Exists(appFile))
        {
            var configured = JsonSerializer.Deserialize<TelegramCredentials>(File.ReadAllText(appFile), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (configured != null && IsValid(configured)) return configured;
        }
        var saved = Load();
        return saved != null && IsValid(saved) ? saved : null;
    }
    public static bool IsValid(TelegramCredentials credentials) => credentials.ApiId > 0
        && credentials.ApiHash is { Length: 32 } && credentials.ApiHash.All(Uri.IsHexDigit);
    public static void Save(TelegramCredentials credentials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathValue)!);
        File.WriteAllBytes(PathValue, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(credentials), null, DataProtectionScope.CurrentUser));
    }
}

public static class TelegramPhoneNumber
{
    public static string Normalize(string input)
    {
        var cleaned = new string(input.Where(c => !char.IsWhiteSpace(c) && c is not ('(' or ')' or '-')).ToArray());
        if (cleaned.StartsWith('+')) cleaned = cleaned[1..];
        if (cleaned.Length is < 7 or > 15 || cleaned.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("Введи номер в международном формате: +, код страны и номер.");
        return "+" + cleaned;
    }
}
