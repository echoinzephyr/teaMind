using System.Globalization;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using TeaMind.Core;
using Windows.UI;
using Windows.UI.Text;

namespace TeaMind.App;

public sealed class NativeTextMeasurer(CanvasDevice device) : ITextMeasurer
{
    private readonly Dictionary<(string, string, double, bool, bool), double> cache = [];
    public double Measure(string text, string family, double size, bool bold, bool italic)
    {
        var key = (text, family, size, bold, italic);
        if (cache.TryGetValue(key, out var width)) return width;
        using var format = SceneRenderer.TextFormat(family, size, bold, italic);
        using var layout = new CanvasTextLayout(device, text, format, 10000, 200);
        width = layout.LayoutBoundsIncludingTrailingWhitespace.Width;
        if (cache.Count > 50000) cache.Clear();
        cache[key] = width; return width;
    }
}
public static class SceneRenderer
{
    public static Color Color(string hex)
    {
        try { var value = uint.Parse(hex.TrimStart('#'), NumberStyles.HexNumber); return Windows.UI.Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value); }
        catch { return Microsoft.UI.Colors.Gray; }
    }
    public static CanvasTextFormat TextFormat(string family, double size, bool bold = false, bool italic = false) => new()
    {
        FontFamily = family, FontSize = (float)size, FontWeight = new() { Weight = (ushort)(bold ? 700 : 400) },
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal, WordWrapping = CanvasWordWrapping.NoWrap
    };
    public static void Draw(CanvasDrawingSession ds, MapScene scene, IReadOnlyDictionary<string, CanvasBitmap> bitmaps, HashSet<string>? selection = null, RectD? viewport = null, string? editingId = null, double detailScale = 1)
    {
        using var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        foreach (var group in scene.Groups)
        {
            var r = group.Bounds;
            if (viewport.HasValue && !viewport.Value.Intersects(r.Inflate(30))) continue;
            if (group.Summary)
            {
                ds.DrawLine((float)r.Right + 8, (float)r.Y, (float)r.Right + 8, (float)r.Bottom, Color(group.Color), 2);
                ds.DrawLine((float)r.Right, (float)r.Y, (float)r.Right + 8, (float)r.Y, Color(group.Color), 2);
                ds.DrawLine((float)r.Right, (float)r.Bottom, (float)r.Right + 8, (float)r.Bottom, Color(group.Color), 2);
            }
            else
            {
                var c = Color(group.Color); c.A = 16; ds.FillRoundedRectangle(ToRect(r), 12, 12, c);
                ds.DrawRoundedRectangle(ToRect(r), 12, 12, Color(group.Color), 1.5f, dash);
            }
            using var format = TextFormat("Microsoft YaHei UI", 12);
            ds.DrawText(group.Text, (float)r.X + 8, (float)r.Y - 22, Color(group.Color), format);
        }
        foreach (var edge in scene.Edges)
        {
            if (viewport.HasValue)
            {
                var left = Math.Min(Math.Min(edge.Start.X, edge.End.X), Math.Min(edge.Control1.X, edge.Control2.X));
                var top = Math.Min(Math.Min(edge.Start.Y, edge.End.Y), Math.Min(edge.Control1.Y, edge.Control2.Y));
                var right = Math.Max(Math.Max(edge.Start.X, edge.End.X), Math.Max(edge.Control1.X, edge.Control2.X));
                var bottom = Math.Max(Math.Max(edge.Start.Y, edge.End.Y), Math.Max(edge.Control1.Y, edge.Control2.Y));
                if (!viewport.Value.Intersects(new RectD(left, top, right - left, bottom - top).Inflate(30))) continue;
            }
            using var path = new CanvasPathBuilder(ds);
            path.BeginFigure(ToVector(edge.Start)); path.AddCubicBezier(ToVector(edge.Control1), ToVector(edge.Control2), ToVector(edge.End)); path.EndFigure(CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(path);
            ds.DrawGeometry(geometry, Color(edge.Color), edge.Dashed ? 1.5f : 2, edge.Dashed ? dash : null);
            if (edge.Dashed)
            {
                var vector = Vector2.Normalize(ToVector(edge.End) - ToVector(edge.Control2));
                if (!float.IsNaN(vector.X))
                {
                    var end = ToVector(edge.End); var perpendicular = new Vector2(-vector.Y, vector.X);
                    ds.DrawLine(end, end - vector * 9 + perpendicular * 4, Color(edge.Color), 1.5f);
                    ds.DrawLine(end, end - vector * 9 - perpendicular * 4, Color(edge.Color), 1.5f);
                }
            }
            if (edge.Text.Length > 0)
            {
                var p = CurveMiddle(edge); using var format = TextFormat("Microsoft YaHei UI", 12);
                ds.DrawText(edge.Text, (float)p.X, (float)p.Y - 20, Color(edge.Color), format);
            }
        }
        foreach (var node in scene.Nodes)
        {
            if (viewport.HasValue && !viewport.Value.Intersects(node.Bounds.Inflate(12))) continue;
            var r = node.Bounds; var radius = (float)Math.Clamp(node.Topic.Style.Radius, 0, 30);
            ds.FillRoundedRectangle(ToRect(r), radius, radius, Color(node.Fill));
            ds.DrawRoundedRectangle(ToRect(r), radius, radius, Color(node.Topic.Id == editingId ? "#4E9B78" : node.Stroke), node.Topic.Id == editingId ? 2 : (float)node.Topic.Style.StrokeWidth);
            if (selection?.Contains(node.Topic.Id) == true)
                ds.DrawRoundedRectangle(ToRect(r.Inflate(4)), radius + 3, radius + 3, Color("#4E9B78"), 2);
            if (node.Topic.Image is { } image && bitmaps.TryGetValue(image, out var bitmap))
                ds.DrawImage(bitmap, ImageRect(node, bitmap.Size.Width, bitmap.Size.Height));
            using var format = TextFormat(node.FontFamily, node.FontSize, node.Bold, node.Italic);
            if (node.Topic.Id != editingId && node.FontSize * detailScale >= 5)
                for (var i = 0; i < node.Lines.Count; i++) ds.DrawText(node.Lines[i], (float)r.X + 18, (float)(node.TextTop + i * node.LineHeight), Color(node.Foreground), format);
            if (node.Badges.Length > 0 && 11 * detailScale >= 5)
            {
                using var small = TextFormat("Microsoft YaHei UI", 11);
                ds.DrawText(node.Badges, (float)r.X + 16, (float)r.Bottom - 26, Color(node.Foreground), small);
            }
            if (node.Topic.Children.Count > 0)
            {
                ds.FillCircle((float)r.Right + 9, (float)r.Center.Y, 8, Color(node.Fill));
                ds.DrawCircle((float)r.Right + 9, (float)r.Center.Y, 8, Color(node.Stroke), 1);
                ds.DrawLine((float)r.Right + 5, (float)r.Center.Y, (float)r.Right + 13, (float)r.Center.Y, Color(node.Stroke), 1);
                if (node.Topic.Collapsed) ds.DrawLine((float)r.Right + 9, (float)r.Center.Y - 4, (float)r.Right + 9, (float)r.Center.Y + 4, Color(node.Stroke), 1);
            }
        }
    }
    public static PointD CurveMiddle(VisualEdge e) => new((e.Start.X + e.Control1.X * 3 + e.Control2.X * 3 + e.End.X) / 8, (e.Start.Y + e.Control1.Y * 3 + e.Control2.Y * 3 + e.End.Y) / 8);
    public static Windows.Foundation.Rect ImageRect(VisualNode node, double width, double height)
    {
        var scale = Math.Min((node.Bounds.Width - 24) / width, 88 / height); var w = width * scale; var h = height * scale;
        return new(node.Bounds.Center.X - w / 2, node.Bounds.Y + 8 + (88 - h) / 2, w, h);
    }
    public static Windows.Foundation.Rect ToRect(RectD r) => new(r.X, r.Y, r.Width, r.Height);
    public static Vector2 ToVector(PointD p) => new((float)p.X, (float)p.Y);
}
