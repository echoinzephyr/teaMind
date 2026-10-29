using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace TeaMind.Core;

public sealed record ConversionResult(MapDocument Document, IReadOnlyList<string> Warnings);
public interface IDocumentConverter
{
    ConversionResult Import(string path);
    IReadOnlyList<string> Export(MapDocument document, string path);
}
public static class SafeArchive
{
    public const long MaxBytes = 256L * 1024 * 1024;
    public static Dictionary<string, byte[]> Read(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 10000) throw new InvalidDataException("压缩包文件过多。");
        var result = new Dictionary<string, byte[]>(); long bytes = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            ValidateName(name);
            if (name.EndsWith('/')) continue;
            bytes += entry.Length;
            if (bytes > MaxBytes) throw new InvalidDataException("解压后的文档超过 256 MB 限制。");
            using var input = entry.Open(); using var memory = new MemoryStream();
            var buffer = new byte[81920]; int read;
            while ((read = input.Read(buffer)) > 0)
            {
                if (memory.Length + read > entry.Length || memory.Length + read > MaxBytes) throw new InvalidDataException("压缩包大小异常。");
                memory.Write(buffer, 0, read);
            }
            if (!result.TryAdd(name, memory.ToArray())) throw new InvalidDataException("压缩包存在重复文件。");
        }
        return result;
    }
    public static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p is ".." or "."))
            throw new InvalidDataException("压缩包包含不安全的路径。");
    }
    public static void Write(string path, IReadOnlyDictionary<string, byte[]> entries)
    {
        if (entries.Count > 10000 || entries.Values.Sum(bytes => (long)bytes.Length) > MaxBytes)
            throw new InvalidDataException("文档资源超过 256 MB 或 10,000 个文件限制，未覆盖原文件。");
        AtomicFile.Write(path, stream =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, true);
            foreach (var (name, bytes) in entries)
            {
                ValidateName(name);
                using var target = archive.CreateEntry(name, CompressionLevel.Fastest).Open(); target.Write(bytes);
            }
        });
    }
}
public static class AtomicFile
{
    public static void Write(string path, Action<Stream> write)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { write(stream); stream.Flush(true); }
            File.Move(temp, full, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void WriteText(string path, string text) => Write(path, s => s.Write(Encoding.UTF8.GetBytes(text)));
}
public sealed class NativeDocumentStore : IDocumentConverter
{
    public ConversionResult Import(string path)
    {
        var entries = SafeArchive.Read(path);
        if (!entries.TryGetValue("document.json", out var json)) throw new InvalidDataException("不是有效的 teaMind 文档。");
        var document = ModelJson.Deserialize<MapDocument>(Encoding.UTF8.GetString(json));
        document.Assets = entries.Where(e => e.Key.StartsWith("assets/", StringComparison.Ordinal)).ToDictionary();
        document.XmindEntries = entries.Where(e => e.Key.StartsWith("xmind/", StringComparison.Ordinal)).ToDictionary(e => e.Key[6..], e => e.Value);
        DocumentValidator.Validate(document);
        return new(document, document.CompatibilityWarnings);
    }
    public IReadOnlyList<string> Export(MapDocument document, string path)
    {
        DocumentValidator.Validate(document);
        var json = JsonSerializerWithoutResources(document);
        var entries = new Dictionary<string, byte[]> { ["document.json"] = Encoding.UTF8.GetBytes(json) };
        foreach (var (name, bytes) in document.Assets) { SafeArchive.ValidateName(name); if (!name.StartsWith("assets/")) throw new InvalidDataException("无效的资源路径。"); entries.Add(name, bytes); }
        foreach (var (name, bytes) in document.XmindEntries) entries.Add("xmind/" + name, bytes);
        SafeArchive.Write(path, entries); return [];
    }
    private static string JsonSerializerWithoutResources(MapDocument document)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(document, ModelJson.Options)!.AsObject();
        node.Remove("assets"); node.Remove("xmindEntries"); return node.ToJsonString(ModelJson.Options);
    }
}
public sealed record RecoveryInfo(string RecoveryId, string Title, string? OriginalPath, string SnapshotPath, DateTime SavedAt);
public sealed class RecoveryStore(string directory)
{
    private readonly NativeDocumentStore store = new();
    public void Save(DocumentSession session)
        => SaveSnapshot(session.RecoveryId, session.FilePath, session.Document);
    public void SaveSnapshot(string recoveryId, string? originalPath, MapDocument document)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, recoveryId + ".teamind");
        store.Export(document, path);
        AtomicFile.WriteText(Path.Combine(directory, recoveryId + ".json"), ModelJson.Serialize(new RecoveryInfo(recoveryId, document.Title, originalPath, path, DateTime.Now)));
    }
    public List<RecoveryInfo> List()
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<RecoveryInfo>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try { var info = ModelJson.Deserialize<RecoveryInfo>(File.ReadAllText(file)); if (File.Exists(info.SnapshotPath)) result.Add(info); }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException) { }
        }
        return result.OrderByDescending(r => r.SavedAt).ToList();
    }
    public MapDocument Read(RecoveryInfo info) => store.Import(info.SnapshotPath).Document;
    public void Remove(string id)
    {
        if (id.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("无效的恢复标识。");
        foreach (var extension in new[] { ".json", ".teamind" })
        {
            var path = Path.Combine(directory, id + extension); if (File.Exists(path)) File.Delete(path);
        }
    }
}
