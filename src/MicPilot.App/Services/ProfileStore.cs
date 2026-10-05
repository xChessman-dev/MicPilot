using System.Text.Json;
using MicPilot.Core;

namespace MicPilot.App.Services;

public sealed record AppState(string? InputId, string? OutputId, string ProfileName, MicSettings Settings, MicProfile[] Profiles);
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _directory;
    public string Warning { get; private set; } = "";
    public ProfileStore(string directory) => _directory = directory;
    public AppState Load()
    {
        var path = Path.Combine(_directory, "settings.json");
        if (!File.Exists(path)) return new(null, null, "Естественный", new(), []);
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Файл настроек слишком большой.");
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path)) ?? throw new InvalidDataException("Пустые настройки.");
            if (state.Settings is null || state.Profiles is null || state.ProfileName is null || state.ProfileName.Length > 64)
                throw new InvalidDataException("Некорректная структура настроек.");
            if (state.Profiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 64 || p.Settings is null))
                throw new InvalidDataException("Некорректный сохранённый профиль.");
            return state with { Settings = state.Settings.Sanitize(), Profiles = state.Profiles.Take(100).Select(p => p with { Settings = p.Settings.Sanitize() }).ToArray() };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or ArgumentException or NullReferenceException)
        { Warning = "Не удалось прочитать настройки. Исходный файл сохранён; новые настройки не записываются до явного сохранения. " + e.Message; return new(null, null, "Естественный", new(), []); }
    }
    public void Save(AppState state, bool explicitSave = false)
    {
        if (Warning.Length > 0 && !explicitSave) return;
        Directory.CreateDirectory(_directory); var path = Path.Combine(_directory, "settings.json"); var temp = path + ".new";
        if (explicitSave && File.Exists(path) && Warning.Length > 0) File.Copy(path, path + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), false);
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json)); File.Move(temp, path, true); Warning = "";
    }
    public static MicProfile Import(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Профиль больше 64 КБ.");
        var profile = JsonSerializer.Deserialize<MicProfile>(File.ReadAllText(path)) ?? throw new InvalidDataException("Пустой профиль.");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 64 || profile.Settings is null) throw new InvalidDataException("Некорректный профиль.");
        return profile with { Settings = profile.Settings.Sanitize() };
    }
    public static void Export(string path, MicProfile profile) => File.WriteAllText(path, JsonSerializer.Serialize(profile, Json));
}
