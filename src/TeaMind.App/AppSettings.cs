using System.Security.Cryptography;
using System.Text;
using TeaMind.Core;

namespace TeaMind.App;

public sealed class AppSettings
{
    private static readonly Lazy<string> dataDirectory = new(ResolveDataDirectory);
    public static string DataDirectory => dataDirectory.Value;
    private static string ResolveDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("TEAMIND_DATA_DIRECTORY");
        var candidates = string.IsNullOrWhiteSpace(configured)
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "teaMind"), Path.Combine(AppContext.BaseDirectory, "Data") }
            : new[] { Path.GetFullPath(configured) };
        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, ".write-" + Guid.NewGuid().ToString("N"));
                using (File.Create(probe)) { } File.Delete(probe); return candidate;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        throw new IOException("无法创建 teaMind 数据目录，请设置 TEAMIND_DATA_DIRECTORY 为可写目录。");
    }
    public string Theme { get; set; } = "System";
    public AiSettings Ai { get; set; } = new();
    public List<string> RecentFiles { get; set; } = [];
    public List<string> Favorites { get; set; } = [];
    public static AppSettings Load()
    {
        try { return ModelJson.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(DataDirectory, "settings.json"))); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException) { return new(); }
    }
    public void Save() => AtomicFile.WriteText(Path.Combine(DataDirectory, "settings.json"), ModelJson.Serialize(this));
    public void SetKey(string key) => Ai.ProtectedKey = key.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
    public string GetKey()
    {
        if (Ai.ProtectedKey.Length == 0) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Ai.ProtectedKey), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return ""; }
    }
    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)); RecentFiles.Insert(0, path);
        RecentFiles = RecentFiles.Take(12).ToList(); Save();
    }
}
