namespace TeaMind.Core;

public readonly record struct PointD(double X, double Y);
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public PointD Center => new(X + Width / 2, Y + Height / 2);
    public bool Contains(PointD point) => point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;
    public RectD Inflate(double amount) => new(X - amount, Y - amount, Width + amount * 2, Height + amount * 2);
    public bool Intersects(RectD other) => X <= other.Right && Right >= other.X && Y <= other.Bottom && Bottom >= other.Y;
    public static RectD Union(IEnumerable<RectD> rectangles)
    {
        var all = rectangles.ToArray();
        if (all.Length == 0) return new(0, 0, 1, 1);
        var left = all.Min(r => r.X); var top = all.Min(r => r.Y);
        return new(left, top, all.Max(r => r.Right) - left, all.Max(r => r.Bottom) - top);
    }
}
public sealed record ThemePalette(string Id, string Name, string Background, string Root, string Text, string Node, string Border, string[] Branches)
{
    public static ThemePalette[] All { get; } = [
        new("tea", "春日茶园", "#FFFFFF", "#356451", "#263D34", "#F1F7F2", "#D9E3DB", ["#5A8B70", "#CF9A57", "#8094BB", "#AC82A6", "#6EA4A1"]),
        new("ocean", "晴空海岸", "#F4F7FC", "#315BA1", "#243854", "#FFFFFF", "#D8E1EE", ["#5884C4", "#58A9AD", "#AB87C5", "#D6A456"]),
        new("ink", "水墨留白", "#FAFAFA", "#30343A", "#30343A", "#FFFFFF", "#D1D5DB", ["#626B77", "#939CA8", "#6F8190"]),
        new("night", "静夜微光", "#1C2424", "#86B9A0", "#E3ECE6", "#293434", "#495A53", ["#86B9A0", "#D3AC75", "#91A8D2", "#C69ABF"])
    ];
    public static ThemePalette Get(string id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}
public sealed class VisualNode
{
    public required Topic Topic { get; init; }
    public RectD Bounds { get; set; }
    public string Fill { get; init; } = "#FFFFFF";
    public string Foreground { get; init; } = "#263D34";
    public string Stroke { get; init; } = "#5A8B70";
    public string FontFamily { get; init; } = "Microsoft YaHei UI";
    public double FontSize { get; init; } = 15;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public List<string> Lines { get; init; } = [];
    public string Badges { get; init; } = "";
    public double LineHeight => FontSize * 1.45;
    public double TextTop => Bounds.Y + 13 + (Topic.Image == null ? 0 : 100);
}
public sealed record VisualEdge(PointD Start, PointD Control1, PointD Control2, PointD End, string Color, bool Dashed = false, string Text = "");
public sealed record VisualGroup(RectD Bounds, string Text, string Color, bool Summary);
public sealed class MapScene
{
    public List<VisualNode> Nodes { get; } = [];
    public List<VisualEdge> Edges { get; } = [];
    public List<VisualGroup> Groups { get; } = [];
    public string Background { get; init; } = "#F6F8F5";
    public RectD Bounds => RectD.Union(Nodes.Select(n => n.Bounds).Concat(Groups.Select(g => g.Bounds.Inflate(30)))).Inflate(65);
    public VisualNode? HitTest(PointD point) => Nodes.LastOrDefault(n => n.Bounds.Contains(point));
}
public interface ITextMeasurer { double Measure(string text, string family, double size, bool bold, bool italic); }
public sealed class ApproximateTextMeasurer : ITextMeasurer
{
    public double Measure(string text, string family, double size, bool bold, bool italic) => text.EnumerateRunes().Sum(r => r.Value > 255 ? size : size * 0.57);
}
public interface ILayoutEngine { MapScene Layout(MapSheet sheet, ITextMeasurer? measurer = null, IReadOnlyDictionary<string, string>? textOverrides = null); }
public sealed class MapLayoutEngine : ILayoutEngine
{
    public MapScene Layout(MapSheet sheet, ITextMeasurer? measurer = null, IReadOnlyDictionary<string, string>? textOverrides = null)
    {
        measurer ??= new ApproximateTextMeasurer();
        var palette = ThemePalette.Get(sheet.Theme);
        var scene = new MapScene { Background = palette.Background };
        var visuals = new Dictionary<string, VisualNode>();
        var heights = new Dictionary<string, double>();
        var widths = new Dictionary<string, double>();
        Make(sheet.Root, 0, palette.Root);
        foreach (var floating in sheet.FloatingTopics) Make(floating, 1, palette.Branches[0]);
        Measure(sheet.Root);
        var root = visuals[sheet.Root.Id];
        root.Bounds = root.Bounds with { X = -root.Bounds.Width / 2, Y = -root.Bounds.Height / 2 };
        if (!sheet.Root.Collapsed)
        {
            var children = sheet.Root.Children;
            if (sheet.Layout is MapLayout.Organization or MapLayout.Tree) PlaceDown(sheet.Root, 0, root.Bounds.Bottom + 65);
            else if (sheet.Layout is MapLayout.Timeline or MapLayout.Fishbone) PlaceTimeline();
            else
            {
                var sides = MindMapBalancer.Resolve(sheet, measurer);
                var left = children.Where(t => sheet.Layout == MapLayout.Left || sheet.Layout == MapLayout.MindMap && sides[t.Id] == TopicSide.Left).ToList();
                var right = children.Except(left).ToList();
                PlaceSide(right, 1); PlaceSide(left, -1);
            }
        }
        foreach (var floating in sheet.FloatingTopics)
        {
            Measure(floating);
            PlaceHorizontal(floating, floating.X ?? scene.Bounds.Right + 60, floating.Y ?? 0, 1);
        }
        AddGroups(sheet.Boundaries, false); AddGroups(sheet.Summaries, true);
        foreach (var relation in sheet.Relationships)
        {
            if (!visuals.TryGetValue(relation.From, out var a) || !visuals.TryGetValue(relation.To, out var b)) continue;
            var start = new PointD(a.Bounds.Center.X, a.Bounds.Y);
            var end = new PointD(b.Bounds.Center.X, b.Bounds.Y);
            var top = Math.Min(start.Y, end.Y) - 65;
            scene.Edges.Add(new(start, new(start.X, top), new(end.X, top), end, relation.Color, true, relation.Text));
        }
        return scene;

        void Make(Topic topic, int depth, string branchColor)
        {
            var size = topic.Style.FontSize ?? (depth == 0 ? 23 : 15);
            var family = topic.Style.FontFamily ?? "Microsoft YaHei UI";
            var bold = topic.Style.Bold || depth == 0;
            var text = textOverrides?.GetValueOrDefault(topic.Id) ?? topic.Text;
            var lines = Wrap(text, 255, family, size, bold, topic.Style.Italic, measurer);
            var badges = string.Join("  ", topic.Markers.Concat(topic.Tags.Select(t => "#" + t)));
            if (topic.Notes.Length > 0) badges += "  ≡";
            if (topic.Link.Length > 0) badges += "  ↗";
            if (topic.Attachments.Count > 0) badges += "  ⌕ " + topic.Attachments.Count;
            if (topic.Collapsed && topic.Children.Count > 0) badges += "  +" + topic.Children.Count;
            var width = Math.Clamp(lines.Max(l => measurer.Measure(l, family, size, bold, topic.Style.Italic)) + 36, depth == 0 ? 160 : 105, 305);
            if (topic.Image != null) width = Math.Max(width, 175);
            if (badges.Length > 0) width = Math.Max(width, Math.Min(290, measurer.Measure(badges, family, 11, false, false) + 32));
            var height = 26 + lines.Count * size * 1.45 + (badges.Length > 0 ? 23 : 0) + (topic.Image != null ? 100 : 0);
            var node = new VisualNode { Topic = topic, Bounds = new(0, 0, width, height), Fill = topic.Style.Fill ?? (depth == 0 ? palette.Root : palette.Node),
                Foreground = topic.Style.Foreground ?? (depth == 0 && sheet.Theme != "night" ? "#FFFFFF" : depth == 0 ? "#183529" : palette.Text),
                Stroke = topic.Style.Stroke ?? (depth == 0 ? palette.Root : branchColor), FontFamily = family, FontSize = size, Bold = bold, Italic = topic.Style.Italic,
                Lines = lines, Badges = badges.Trim() };
            scene.Nodes.Add(node); visuals.Add(topic.Id, node);
            if (!topic.Collapsed)
                for (var i = 0; i < topic.Children.Count; i++) Make(topic.Children[i], depth + 1, depth == 0 ? palette.Branches[i % palette.Branches.Length] : branchColor);
        }
        void Measure(Topic topic)
        {
            var node = visuals[topic.Id];
            var children = topic.Collapsed ? [] : topic.Children;
            foreach (var child in children) Measure(child);
            heights[topic.Id] = Math.Max(node.Bounds.Height, children.Sum(c => heights[c.Id] + 24) - (children.Count > 0 ? 24 : 0));
            widths[topic.Id] = Math.Max(node.Bounds.Width, children.Sum(c => widths[c.Id] + 28) - (children.Count > 0 ? 28 : 0));
        }
        void PlaceSide(List<Topic> children, int direction)
        {
            var y = -(children.Sum(c => heights[c.Id] + 24) - 24) / 2;
            foreach (var child in children)
            {
                var node = visuals[child.Id];
                PlaceHorizontal(child, direction > 0 ? root.Bounds.Right + 70 : root.Bounds.X - 70 - node.Bounds.Width, y + heights[child.Id] / 2, direction);
                Connect(root, node, false); y += heights[child.Id] + 24;
            }
        }
        void PlaceHorizontal(Topic topic, double x, double centerY, int direction)
        {
            var node = visuals[topic.Id]; node.Bounds = node.Bounds with { X = x, Y = centerY - node.Bounds.Height / 2 };
            if (topic.Collapsed) return;
            var y = centerY - (topic.Children.Sum(c => heights[c.Id] + 24) - 24) / 2;
            foreach (var child in topic.Children)
            {
                var c = visuals[child.Id];
                PlaceHorizontal(child, direction > 0 ? node.Bounds.Right + 60 : node.Bounds.X - 60 - c.Bounds.Width, y + heights[child.Id] / 2, direction);
                Connect(node, c, false); y += heights[child.Id] + 24;
            }
        }
        void PlaceDown(Topic topic, double centerX, double top)
        {
            if (topic.Collapsed || topic.Children.Count == 0) return;
            var total = topic.Children.Sum(c => widths[c.Id] + 28) - 28;
            var x = centerX - total / 2;
            // A common row baseline prevents tall text nodes from overlapping the following level.
            var nextTop = top + topic.Children.Max(c => visuals[c.Id].Bounds.Height) + 65;
            foreach (var child in topic.Children)
            {
                var n = visuals[child.Id];
                n.Bounds = n.Bounds with { X = x + widths[child.Id] / 2 - n.Bounds.Width / 2, Y = top };
                Connect(visuals[topic.Id], n, true); PlaceDown(child, n.Bounds.Center.X, nextTop); x += widths[child.Id] + 28;
            }
        }
        void PlaceTimeline()
        {
            var x = root.Bounds.Right + 100;
            var previous = root;
            for (var i = 0; i < sheet.Root.Children.Count; i++)
            {
                var topic = sheet.Root.Children[i]; var node = visuals[topic.Id];
                var direction = i % 2 == 0 ? -1 : 1;
                var y = sheet.Layout == MapLayout.Fishbone ? direction * (110 + heights[topic.Id] / 2) : 0;
                PlaceHorizontal(topic, x, y, 1);
                if (sheet.Layout == MapLayout.Fishbone)
                {
                    scene.Edges.Add(new(new(x - 45, 0), new(x - 45, 0), new(x - 45, y), node.Bounds.Center, node.Stroke));
                    scene.Edges.Add(new(previous.Bounds.Center, previous.Bounds.Center, new(x - 45, 0), new(x - 45, 0), palette.Root));
                }
                else Connect(previous, node, false);
                var subtree = topic.DescendantsAndSelf().Where(t => visuals.ContainsKey(t.Id)).Select(t => visuals[t.Id].Bounds);
                x = RectD.Union(subtree).Right + 100; previous = node;
            }
        }
        void Connect(VisualNode a, VisualNode b, bool vertical)
        {
            PointD start, end, c1, c2;
            if (vertical)
            {
                start = new(a.Bounds.Center.X, a.Bounds.Bottom); end = new(b.Bounds.Center.X, b.Bounds.Y);
                c1 = new(start.X, (start.Y + end.Y) / 2); c2 = new(end.X, c1.Y);
            }
            else
            {
                var right = b.Bounds.Center.X > a.Bounds.Center.X;
                start = new(right ? a.Bounds.Right : a.Bounds.X, a.Bounds.Center.Y);
                end = new(right ? b.Bounds.X : b.Bounds.Right, b.Bounds.Center.Y);
                c1 = new((start.X + end.X) / 2, start.Y); c2 = new(c1.X, end.Y);
            }
            scene.Edges.Add(new(start, c1, c2, end, b.Stroke));
        }
        void AddGroups(List<TopicGroup> groups, bool summary)
        {
            foreach (var group in groups)
            {
                if (sheet.Find(group.ParentId) is not { Collapsed: false } parent) continue;
                var rects = parent.Children.Skip(group.Start).Take(group.End - group.Start + 1).SelectMany(t => t.DescendantsAndSelf())
                    .Where(t => visuals.ContainsKey(t.Id)).Select(t => visuals[t.Id].Bounds).ToList();
                if (rects.Count > 0) scene.Groups.Add(new(RectD.Union(rects).Inflate(15), group.Text, group.Color, summary));
            }
        }
    }
    internal static List<string> Wrap(string text, double maxWidth, string family, double size, bool bold, bool italic, ITextMeasurer measurer)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var rune in paragraph.EnumerateRunes())
            {
                var next = line + rune;
                if (line.Length > 0 && measurer.Measure(next, family, size, bold, italic) > maxWidth) { lines.Add(line); line = rune.ToString(); }
                else line = next;
            }
            lines.Add(line);
        }
        return lines.Count == 0 ? [""] : lines;
    }
}
public static class MindMapBalancer
{
    public static Dictionary<string, TopicSide> Resolve(MapSheet sheet, ITextMeasurer? measurer = null)
    {
        measurer ??= new ApproximateTextMeasurer();
        var heights = sheet.Root.Children.ToDictionary(t => t.Id, t => BranchHeight(t, measurer));
        var result = sheet.Root.Children.Where(t => t.Side != TopicSide.Auto).ToDictionary(t => t.Id, t => t.Side);
        var left = result.Where(p => p.Value == TopicSide.Left).Sum(p => heights[p.Key] + 24);
        var right = result.Where(p => p.Value == TopicSide.Right).Sum(p => heights[p.Key] + 24);
        foreach (var topic in sheet.Root.Children.Where(t => t.Side == TopicSide.Auto))
        {
            var side = left < right ? TopicSide.Left : TopicSide.Right;
            result[topic.Id] = side;
            if (side == TopicSide.Left) left += heights[topic.Id] + 24; else right += heights[topic.Id] + 24;
        }
        return result;
    }
    public static void Assign(MapSheet sheet)
    {
        var sides = Resolve(sheet);
        foreach (var topic in sheet.Root.Children) topic.Side = sides[topic.Id];
    }
    public static void Rebalance(MapSheet sheet)
    {
        foreach (var topic in sheet.Root.Children) topic.Side = TopicSide.Auto;
        Assign(sheet);
    }
    private static double BranchHeight(Topic topic, ITextMeasurer measurer)
    {
        var size = topic.Style.FontSize ?? 15;
        var lines = MapLayoutEngine.Wrap(topic.Text, 255, topic.Style.FontFamily ?? "Microsoft YaHei UI", size, topic.Style.Bold, topic.Style.Italic, measurer);
        var badges = topic.Tags.Count + topic.Markers.Count + topic.Attachments.Count > 0 || topic.Notes.Length > 0 || topic.Link.Length > 0 || topic.Collapsed && topic.Children.Count > 0;
        var own = 26 + lines.Count * size * 1.45 + (badges ? 23 : 0) + (topic.Image != null ? 100 : 0);
        if (topic.Collapsed || topic.Children.Count == 0) return own;
        return Math.Max(own, topic.Children.Sum(t => BranchHeight(t, measurer) + 24) - 24);
    }
}
