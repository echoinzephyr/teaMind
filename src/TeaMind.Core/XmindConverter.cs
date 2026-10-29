using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TeaMind.Core;

public sealed class XmindConverter : IDocumentConverter
{
    public ConversionResult Import(string path)
    {
        var entries = SafeArchive.Read(path);
        if (!entries.TryGetValue("content.json", out var bytes)) throw new InvalidDataException("仅支持现代 Xmind JSON 格式；暂不支持 Xmind 8 的 XML 文件。");
        if (JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 256 }) is not JsonArray sheets) throw new InvalidDataException("Xmind 画布数据无效。");
        var warnings = new HashSet<string> { "Xmind 主题、特殊结构和自定义连线路径可能与原图不同，请检查兼容性报告与导出结果。" };
        var document = new MapDocument { Title = Path.GetFileNameWithoutExtension(path), Sheets = [], XmindEntries = entries };
        foreach (var (name, content) in entries.Where(e => e.Key.StartsWith("resources/", StringComparison.Ordinal))) document.Assets["assets/xmind/" + name] = content;
        foreach (var item in sheets.OfType<JsonObject>())
        {
            var rootData = item["rootTopic"] as JsonObject ?? throw new InvalidDataException("Xmind 缺少中心主题。");
            var sheet = new MapSheet { Id = String(item, "id", Guid.NewGuid().ToString("N")), Title = String(item, "title", "画布"), XmindData = (JsonObject)item.DeepClone() };
            sheet.Root = ReadTopic(rootData, sheet);
            var structure = String(rootData, "structureClass");
            sheet.Layout = FromStructure(structure);
            if (structure.Length > 0 && !Structures.Contains(structure)) warnings.Add($"结构 {structure} 暂以近似布局显示；原字段保留。更改布局后将转换为所选结构。");
            if (rootData["children"]?["detached"] is JsonArray detached)
                sheet.FloatingTopics = detached.OfType<JsonObject>().Select(t => ReadTopic(t, sheet)).ToList();
            if (item["relationships"] is JsonArray relationships)
                foreach (var r in relationships.OfType<JsonObject>())
                    sheet.Relationships.Add(new() { Id = String(r, "id", Guid.NewGuid().ToString("N")), From = String(r, "end1Id"), To = String(r, "end2Id"), Text = String(r, "title"), XmindData = (JsonObject)r.DeepClone(), Color = String(r["style"]?["properties"], "line-color", "#8B6CB8") });
            if (item["theme"] != null) warnings.Add("内置主题以 teaMind 配色显示；原始 Xmind 主题数据会保留，逐节点显式样式会转换。");
            if (item["extensions"] != null || item["zones"] != null) warnings.Add("扩展数据或区域保留在原始字段中，但不提供完整编辑和显示。");
            document.Sheets.Add(sheet);
        }
        document.CompatibilityWarnings = warnings.ToList();
        DocumentValidator.Validate(document);
        return new(document, document.CompatibilityWarnings);

        Topic ReadTopic(JsonObject obj, MapSheet sheet, int depth = 0)
        {
            if (depth > 100) throw new InvalidDataException("主题层级超过 100 层。");
            var topic = new Topic { Id = String(obj, "id", Guid.NewGuid().ToString("N")), Text = String(obj, "title"), XmindData = (JsonObject)obj.DeepClone(),
                Collapsed = String(obj, "branch") == "folded", Notes = String(obj["notes"]?["plain"], "content"), Link = String(obj, "href") };
            if (obj["labels"] is JsonArray labels) topic.Tags = labels.Select(l => l?.ToString() ?? "").ToList();
            if (obj["markers"] is JsonArray markers) topic.Markers = markers.Select(m => String(m, "markerId")).Where(s => s.Length > 0).ToList();
            var props = obj["style"]?["properties"];
            topic.Style = new() { Fill = NullableString(props, "fill-color"), Foreground = NullableString(props, "fo:color"), Stroke = NullableString(props, "line-color"),
                FontFamily = NullableString(props, "fo:font-family"), FontSize = ParseSize(NullableString(props, "fo:font-size")),
                Bold = String(props, "fo:font-weight") == "bold", Italic = String(props, "fo:font-style") == "italic" };
            if (obj["position"] is JsonObject position) { topic.X = position["x"]?.GetValue<double>(); topic.Y = position["y"]?.GetValue<double>(); }
            var image = String(obj["image"], "src");
            if (image.StartsWith("xap:", StringComparison.OrdinalIgnoreCase))
            {
                var key = "assets/xmind/" + image[4..].TrimStart('/');
                if (document.Assets.ContainsKey(key)) topic.Image = key; else warnings.Add($"主题“{topic.Text}”的图片资源缺失。");
            }
            else if (image.Length > 0) warnings.Add("外部图片链接保留，但不会自动联网下载。");
            if (topic.Link.StartsWith("xap:"))
            {
                var key = "assets/xmind/" + topic.Link[4..].TrimStart('/');
                if (document.Assets.ContainsKey(key)) { topic.Attachments.Add(key); topic.Link = ""; }
            }
            if (obj["children"]?["attached"] is JsonArray children) topic.Children = children.OfType<JsonObject>().Select(c => ReadTopic(c, sheet, depth + 1)).ToList();
            ReadGroups(obj["boundaries"] as JsonArray, sheet.Boundaries, false);
            ReadGroups(obj["summaries"] as JsonArray, sheet.Summaries, true);
            if (obj["extensions"] != null || obj["equations"] != null || obj["notes"]?["realHTML"] != null)
                warnings.Add("部分扩展、公式或富文本备注仅保留原数据，当前显示纯文本内容。");
            return topic;

            void ReadGroups(JsonArray? groups, List<TopicGroup> target, bool summary)
            {
                if (groups == null) return;
                foreach (var g in groups.OfType<JsonObject>())
                {
                    var match = Regex.Match(String(g, "range"), @"\((\d+),(\d+)\)");
                    if (!match.Success) { warnings.Add("无法识别的概要或边界范围已保留原字段，但无法显示。"); continue; }
                    var text = String(g, "title");
                    if (summary && obj["children"]?["summary"] is JsonArray summaries)
                        text = summaries.OfType<JsonObject>().Where(n => String(n, "id") == String(g, "topicId")).Select(n => String(n, "title")).FirstOrDefault() ?? text;
                    target.Add(new() { Id = String(g, "id", Guid.NewGuid().ToString("N")), ParentId = topic.Id, Start = int.Parse(match.Groups[1].Value), End = int.Parse(match.Groups[2].Value), Text = text, XmindData = (JsonObject)g.DeepClone() });
                }
            }
        }
    }
    public IReadOnlyList<string> Export(MapDocument document, string path)
    {
        DocumentValidator.Validate(document);
        var warnings = document.CompatibilityWarnings.ToList();
        var entries = document.XmindEntries.ToDictionary(e => e.Key, e => e.Value);
        var output = new JsonArray();
        foreach (var sheet in document.Sheets)
        {
            var obj = sheet.XmindData?.DeepClone().AsObject() ?? new JsonObject();
            obj.Remove("teaMindGroupWarning");
            obj["id"] = sheet.Id; obj["class"] = "sheet"; obj["title"] = sheet.Title;
            obj["rootTopic"] = WriteTopic(sheet.Root, sheet);
            var originalStructure = String(sheet.Root.XmindData, "structureClass");
            obj["rootTopic"]!["structureClass"] = sheet.Root.XmindData != null && FromStructure(originalStructure) == sheet.Layout && originalStructure.Length > 0 ? originalStructure : ToStructure(sheet.Layout);
            if (sheet.FloatingTopics.Count > 0) obj["rootTopic"]!["children"]!["detached"] = new JsonArray(sheet.FloatingTopics.Select(t => (JsonNode)WriteTopic(t, sheet)).ToArray());
            else (obj["rootTopic"]?["children"] as JsonObject)?.Remove("detached");
            obj["relationships"] = new JsonArray(sheet.Relationships.Select(r =>
            {
                var rel = r.XmindData?.DeepClone().AsObject() ?? new JsonObject();
                rel["id"] = r.Id; rel["class"] = "relationship"; rel["end1Id"] = r.From; rel["end2Id"] = r.To; rel["title"] = r.Text;
                return (JsonNode)rel;
            }).ToArray());
            if (sheet.XmindData?["teaMindGroupWarning"] != null) warnings.Add(sheet.XmindData["teaMindGroupWarning"]!.ToString());
            output.Add(obj);
        }
        entries["content.json"] = Encoding.UTF8.GetBytes(output.ToJsonString(ModelJson.Options));
        entries["metadata.json"] = entries.GetValueOrDefault("metadata.json") ?? Encoding.UTF8.GetBytes("{\"creator\":{\"name\":\"teaMind\",\"version\":\"0.1.0\"}}");
        var manifest = new JsonObject();
        foreach (var key in entries.Keys.Where(k => k != "manifest.json")) manifest[key] = new JsonObject();
        entries["manifest.json"] = Encoding.UTF8.GetBytes(new JsonObject { ["file-entries"] = manifest }.ToJsonString());
        entries.Remove("Thumbnails/thumbnail.png");
        SafeArchive.Write(path, entries);
        return warnings.Distinct().ToList();

        JsonObject WriteTopic(Topic topic, MapSheet sheet)
        {
            var obj = topic.XmindData?.DeepClone().AsObject() ?? new JsonObject();
            obj["id"] = topic.Id; obj["class"] = "topic"; obj["title"] = topic.Text;
            obj["branch"] = topic.Collapsed ? "folded" : "unfolded";
            var children = obj["children"] as JsonObject ?? new JsonObject();
            children["attached"] = new JsonArray(topic.Children.Select(t => (JsonNode)WriteTopic(t, sheet)).ToArray());
            // Detached topics belong to the sheet; never leave stale copies on an edited node.
            children.Remove("detached");
            obj["children"] = children.DeepClone();
            var originalNotes = String(topic.XmindData?["notes"]?["plain"], "content");
            if (topic.Notes != originalNotes || topic.XmindData == null)
                obj["notes"] = topic.Notes.Length == 0 ? null : new JsonObject { ["plain"] = new JsonObject { ["content"] = topic.Notes } };
            obj["labels"] = new JsonArray(topic.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
            obj["markers"] = new JsonArray(topic.Markers.Select(m => (JsonNode)new JsonObject { ["markerId"] = m }).ToArray());
            obj["href"] = topic.Link.Length > 0 ? topic.Link : topic.Attachments.Count > 0 ? "xap:" + AddResource(topic.Attachments[0]) : null;
            if (topic.Attachments.Count > 1) warnings.Add("Xmind 每个主题仅映射首个附件链接，其余附件仍保留在文件资源中。");
            foreach (var attachment in topic.Attachments) AddResource(attachment);
            if (topic.Image != null)
            {
                var image = obj["image"]?.DeepClone().AsObject() ?? new JsonObject(); image["src"] = "xap:" + AddResource(topic.Image); obj["image"] = image;
            }
            else if (topic.XmindData?["image"]?["src"]?.ToString().StartsWith("xap:") == true) obj.Remove("image");
            if (topic.X.HasValue && topic.Y.HasValue) obj["position"] = new JsonObject { ["x"] = topic.X, ["y"] = topic.Y };
            var style = obj["style"]?.DeepClone().AsObject() ?? new JsonObject { ["id"] = Guid.NewGuid().ToString("N") };
            var properties = style["properties"]?.DeepClone().AsObject() ?? new JsonObject();
            Set("fill-color", topic.Style.Fill); Set("fo:color", topic.Style.Foreground); Set("line-color", topic.Style.Stroke);
            Set("fo:font-family", topic.Style.FontFamily); Set("fo:font-size", topic.Style.FontSize?.ToString(CultureInfo.InvariantCulture) + (topic.Style.FontSize.HasValue ? "pt" : ""));
            properties["fo:font-weight"] = topic.Style.Bold ? "bold" : "normal"; properties["fo:font-style"] = topic.Style.Italic ? "italic" : "normal";
            style["properties"] = properties; obj["style"] = style;
            WriteGroups(sheet.Boundaries.Where(g => g.ParentId == topic.Id).ToList(), "boundaries", false);
            WriteGroups(sheet.Summaries.Where(g => g.ParentId == topic.Id).ToList(), "summaries", true);
            return obj;

            void Set(string key, string? value) { if (!string.IsNullOrEmpty(value)) properties[key] = value; else properties.Remove(key); }
            void WriteGroups(List<TopicGroup> groups, string key, bool summary)
            {
                var array = new JsonArray(); var summaryTopics = new JsonArray();
                foreach (var group in groups)
                {
                    if (group.Start < 0 || group.End >= topic.Children.Count || group.End < group.Start) { warnings.Add("无效的概要或边界范围未导出。"); continue; }
                    var g = group.XmindData?.DeepClone().AsObject() ?? new JsonObject();
                    g["id"] = group.Id; g["class"] = summary ? "summary" : "boundary"; g["range"] = $"({group.Start},{group.End})";
                    if (summary)
                    {
                        var summaryId = String(g, "topicId", group.Id + "-topic"); g["topicId"] = summaryId;
                        var prior = (topic.XmindData?["children"]?["summary"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(n => String(n, "id") == summaryId);
                        var summaryTopic = prior?.DeepClone().AsObject() ?? new JsonObject();
                        summaryTopic["id"] = summaryId; summaryTopic["class"] = "topic"; summaryTopic["title"] = group.Text; summaryTopics.Add(summaryTopic);
                    }
                    else g["title"] = group.Text;
                    array.Add(g);
                }
                // Keep unsupported range encodings as opaque data rather than silently dropping them.
                if (topic.XmindData?[key] is JsonArray originals)
                    foreach (var original in originals.OfType<JsonObject>().Where(g => !Regex.IsMatch(String(g, "range"), @"\((\d+),(\d+)\)")))
                    {
                        array.Add(original.DeepClone()); warnings.Add("无法识别的概要或边界范围已原样保留；结构编辑后可能不再适用。");
                        if (summary && topic.XmindData?["children"]?["summary"] is JsonArray originalSummaryTopics)
                            foreach (var originalTopic in originalSummaryTopics.OfType<JsonObject>().Where(t => String(t, "id") == String(original, "topicId"))) summaryTopics.Add(originalTopic.DeepClone());
                    }
                obj[key] = array;
                if (summary) obj["children"]!["summary"] = summaryTopics;
            }
        }
        string AddResource(string key)
        {
            if (!document.Assets.TryGetValue(key, out var content)) { warnings.Add($"资源缺失：{key}"); return key; }
            var target = key.StartsWith("assets/xmind/") ? key[13..] : "resources/" + Path.GetFileName(key);
            entries[target] = content; return target;
        }
    }
    private static string String(JsonNode? node, string key, string fallback = "") => node?[key]?.ToString() ?? fallback;
    private static string? NullableString(JsonNode? node, string key) => node?[key]?.ToString();
    private static double? ParseSize(string? value)
    {
        if (value == null) return null;
        return double.TryParse(Regex.Match(value, @"[\d.]+").Value, CultureInfo.InvariantCulture, out var size) ? Math.Clamp(size, 8, 96) : null;
    }
    private static readonly HashSet<string> Structures = ["org.xmind.ui.map.unbalanced", "org.xmind.ui.logic.right", "org.xmind.ui.logic.left", "org.xmind.ui.org-chart.down", "org.xmind.ui.tree.right", "org.xmind.ui.timeline.horizontal", "org.xmind.ui.fishbone.rightHeaded"];
    private static MapLayout FromStructure(string value) => value switch
    {
        "org.xmind.ui.logic.right" => MapLayout.Right, "org.xmind.ui.logic.left" => MapLayout.Left,
        "org.xmind.ui.org-chart.down" => MapLayout.Organization, "org.xmind.ui.tree.right" => MapLayout.Tree,
        "org.xmind.ui.timeline.horizontal" => MapLayout.Timeline, "org.xmind.ui.fishbone.rightHeaded" => MapLayout.Fishbone, _ => MapLayout.MindMap
    };
    private static string ToStructure(MapLayout layout) => layout switch
    {
        MapLayout.Right => "org.xmind.ui.logic.right", MapLayout.Left => "org.xmind.ui.logic.left", MapLayout.Organization => "org.xmind.ui.org-chart.down",
        MapLayout.Tree => "org.xmind.ui.tree.right", MapLayout.Timeline => "org.xmind.ui.timeline.horizontal", MapLayout.Fishbone => "org.xmind.ui.fishbone.rightHeaded", _ => "org.xmind.ui.map.unbalanced"
    };
}
