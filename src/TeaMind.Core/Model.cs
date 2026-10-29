using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TeaMind.Core;

public enum MapLayout { MindMap, Right, Left, Organization, Tree, Timeline, Fishbone }
public enum TopicSide { Auto, Left, Right }
public sealed class TopicStyle
{
    public string? Fill { get; set; }
    public string? Foreground { get; set; }
    public string? Stroke { get; set; }
    public string? FontFamily { get; set; }
    public double? FontSize { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public double StrokeWidth { get; set; } = 1.5;
    public double Radius { get; set; } = 10;
}
public sealed class Topic
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "新主题";
    public List<Topic> Children { get; set; } = [];
    public bool Collapsed { get; set; }
    public TopicSide Side { get; set; }
    public string Notes { get; set; } = "";
    public string Link { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<string> Markers { get; set; } = [];
    public string? Image { get; set; }
    public List<string> Attachments { get; set; } = [];
    public TopicStyle Style { get; set; } = new();
    public double? X { get; set; }
    public double? Y { get; set; }
    public JsonObject? XmindData { get; set; }
    public IEnumerable<Topic> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var nested in child.DescendantsAndSelf()) yield return nested;
    }
}
public sealed class Relationship
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Text { get; set; } = "关系";
    public string Color { get; set; } = "#8B6CB8";
    public JsonObject? XmindData { get; set; }
}
public sealed class TopicGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ParentId { get; set; } = "";
    public int Start { get; set; }
    public int End { get; set; }
    public string Text { get; set; } = "概要";
    public string Color { get; set; } = "#8B6CB8";
    public JsonObject? XmindData { get; set; }
}
public sealed class MapSheet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "画布 1";
    public Topic Root { get; set; } = new() { Text = "中心主题" };
    public List<Topic> FloatingTopics { get; set; } = [];
    public MapLayout Layout { get; set; }
    public string Theme { get; set; } = "tea";
    public List<Relationship> Relationships { get; set; } = [];
    public List<TopicGroup> Boundaries { get; set; } = [];
    public List<TopicGroup> Summaries { get; set; } = [];
    public JsonObject? XmindData { get; set; }
    public IEnumerable<Topic> AllTopics() => new[] { Root }.Concat(FloatingTopics).SelectMany(x => x.DescendantsAndSelf());
    public Topic? Find(string id) => AllTopics().FirstOrDefault(x => x.Id == id);
    public Topic? ParentOf(string id) => AllTopics().FirstOrDefault(x => x.Children.Any(c => c.Id == id));
}
public sealed class MapDocument
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "未命名导图";
    public List<MapSheet> Sheets { get; set; } = [new()];
    public Dictionary<string, byte[]> Assets { get; set; } = [];
    public Dictionary<string, byte[]> XmindEntries { get; set; } = [];
    public List<string> CompatibilityWarnings { get; set; } = [];
    public static MapDocument CreateWelcome() => new()
    {
        Title = "我的第一张导图",
        Sheets = [new() { Title = "开始探索", Root = new() { Text = "让想法，自由生长", Children = [
            new() { Text = "欢迎使用 teaMind", Children = [new() { Text = "本地优先 · 无需登录" }, new() { Text = "自由编辑 · 无水印导出" }] },
            new() { Text = "从一个想法开始", Children = [new() { Text = "Tab 创建子主题" }, new() { Text = "Enter 创建同级主题" }, new() { Text = "双击文字即可编辑" }] },
            new() { Text = "连接你的灵感", Children = [new() { Text = "拖拽主题调整结构" }, new() { Text = "右侧定制样式与备注" }] },
            new() { Text = "与 AI 一起思考", Children = [new() { Text = "在设置中连接模型服务" }, new() { Text = "预览确认后再写入导图" }] }
        ] } }]
    };
}
public static class ModelJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 256
    };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string text) => JsonSerializer.Deserialize<T>(text, Options) ?? throw new InvalidDataException("文档内容为空。");
    public static T Clone<T>(T value) => Deserialize<T>(Serialize(value));
}
