using System.Globalization;
using System.Numerics;
using System.Security;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using TeaMind.Core;

namespace TeaMind.App;

public sealed class MapExporter
{
    public async Task ExportPngAsync(string path, MapScene scene, CanvasDevice device, IReadOnlyDictionary<string, CanvasBitmap> images, double scale, bool transparent)
    {
        var bounds = scene.Bounds;
        var width = Math.Ceiling(bounds.Width * scale); var height = Math.Ceiling(bounds.Height * scale);
        if (width > 16384 || height > 16384 || width * height > 64000000) throw new InvalidOperationException("图片尺寸过大，请降低清晰度或使用 SVG / PDF 导出。");
        using var target = new CanvasRenderTarget(device, (float)width, (float)height, 96);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(transparent ? Microsoft.UI.Colors.Transparent : SceneRenderer.Color(scene.Background));
            ds.Transform = Matrix3x2.CreateTranslation((float)-bounds.X, (float)-bounds.Y) * Matrix3x2.CreateScale((float)scale);
            SceneRenderer.Draw(ds, scene, images);
        }
        var full = Path.GetFullPath(path); var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await target.SaveAsync(temp, CanvasBitmapFileFormat.Png); File.Move(temp, full, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void ExportSvg(string path, MapScene scene, CanvasDevice device, MapDocument document, bool transparent)
    {
        var b = scene.Bounds; var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"{N(b.Width)}\" height=\"{N(b.Height)}\" viewBox=\"{N(b.X)} {N(b.Y)} {N(b.Width)} {N(b.Height)}\">");
        if (!transparent) sb.Append($"<rect x=\"{N(b.X)}\" y=\"{N(b.Y)}\" width=\"{N(b.Width)}\" height=\"{N(b.Height)}\" fill=\"{C(scene.Background)}\"/>");
        foreach (var g in scene.Groups)
        {
            var r = g.Bounds;
            if (g.Summary) sb.Append($"<path d=\"M{N(r.Right)} {N(r.Y)}h8v{N(r.Height)}h-8\" fill=\"none\" stroke=\"{C(g.Color)}\" stroke-width=\"2\"/>");
            else sb.Append($"<rect x=\"{N(r.X)}\" y=\"{N(r.Y)}\" width=\"{N(r.Width)}\" height=\"{N(r.Height)}\" rx=\"12\" fill=\"{C(g.Color)}\" fill-opacity=\".06\" stroke=\"{C(g.Color)}\" stroke-dasharray=\"5 4\"/>");
            Text(g.Text, r.X + 8, r.Y - 22, "Microsoft YaHei UI", 12, false, false, g.Color);
        }
        foreach (var e in scene.Edges)
        {
            sb.Append($"<path d=\"M{P(e.Start)} C{P(e.Control1)} {P(e.Control2)} {P(e.End)}\" fill=\"none\" stroke=\"{C(e.Color)}\" stroke-width=\"2\"{(e.Dashed ? " stroke-dasharray=\"5 4\"" : "")}/>");
            if (e.Dashed) { var points = Arrow(e); if (points != null) sb.Append($"<path d=\"M{P(points.Value.A)} L{P(e.End)} L{P(points.Value.B)}\" fill=\"none\" stroke=\"{C(e.Color)}\"/>"); }
            var middle = SceneRenderer.CurveMiddle(e); Text(e.Text, middle.X, middle.Y - 20, "Microsoft YaHei UI", 12, false, false, e.Color);
        }
        foreach (var node in scene.Nodes)
        {
            var r = node.Bounds;
            sb.Append($"<rect x=\"{N(r.X)}\" y=\"{N(r.Y)}\" width=\"{N(r.Width)}\" height=\"{N(r.Height)}\" rx=\"{N(node.Topic.Style.Radius)}\" fill=\"{C(node.Fill)}\" stroke=\"{C(node.Stroke)}\" stroke-width=\"{N(node.Topic.Style.StrokeWidth)}\"/>");
            if (node.Topic.Image is { } key && document.Assets.TryGetValue(key, out var image))
            {
                var mime = image.Length > 3 && image[0] == 0xFF && image[1] == 0xD8 ? "image/jpeg" : "image/png";
                sb.Append($"<image x=\"{N(r.X + 12)}\" y=\"{N(r.Y + 8)}\" width=\"{N(r.Width - 24)}\" height=\"88\" preserveAspectRatio=\"xMidYMid meet\" xlink:href=\"data:{mime};base64,{Convert.ToBase64String(image)}\"/>");
            }
            for (var i = 0; i < node.Lines.Count; i++) Text(node.Lines[i], r.X + 18, node.TextTop + i * node.LineHeight, node.FontFamily, node.FontSize, node.Bold, node.Italic, node.Foreground);
            Text(node.Badges, r.X + 16, r.Bottom - 26, "Microsoft YaHei UI", 11, false, false, node.Foreground);
        }
        sb.Append("</svg>"); AtomicFile.WriteText(path, sb.ToString());
        void Text(string value, double x, double y, string family, double size, bool bold, bool italic, string color)
        {
            if (value.Length == 0) return;
            var geometry = Outline(device, value, family, size, bold, italic);
            sb.Append($"<path transform=\"translate({N(x)} {N(y)})\" d=\"{geometry.Svg}\" fill=\"{C(color)}\" fill-rule=\"{(geometry.Alternate ? "evenodd" : "nonzero")}\"><title>{SecurityElement.Escape(value)}</title></path>");
        }
    }
    public void ExportPdf(string path, MapScene scene, CanvasDevice device, MapDocument document, bool paginate)
    {
        using var pdf = new PdfDocument(); pdf.Info.Title = document.Title; pdf.Info.Creator = "teaMind";
        var b = scene.Bounds;
        var scale = paginate ? 0.75 : Math.Min(1, 14000 / Math.Max(b.Width, b.Height));
        var pageWidth = paginate ? 842d : b.Width * scale;
        var pageHeight = paginate ? 595d : b.Height * scale;
        var margin = paginate ? 24d : 0;
        var tileWidth = (pageWidth - margin * 2) / scale; var tileHeight = (pageHeight - margin * 2) / scale;
        var columns = Math.Max(1, (int)Math.Ceiling(b.Width / tileWidth)); var rows = Math.Max(1, (int)Math.Ceiling(b.Height / tileHeight));
        if (columns * rows > 200) throw new InvalidOperationException("分页超过 200 页，请使用整图 PDF 或缩小导图。");
        var outlineCache = new Dictionary<(string, string, double, bool, bool), OutlinePath>();
        for (var row = 0; row < rows; row++) for (var col = 0; col < columns; col++)
        {
            var page = pdf.AddPage(); page.Width = XUnit.FromPoint(pageWidth); page.Height = XUnit.FromPoint(pageHeight);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(Brush(scene.Background), 0, 0, pageWidth, pageHeight);
            gfx.IntersectClip(new XRect(margin, margin, pageWidth - margin * 2, pageHeight - margin * 2));
            gfx.TranslateTransform(margin, margin); gfx.ScaleTransform(scale); gfx.TranslateTransform(-b.X - col * tileWidth, -b.Y - row * tileHeight);
            foreach (var g in scene.Groups)
            {
                var r = g.Bounds; var pen = new XPen(XColor.FromArgb(SceneRenderer.Color(g.Color).R, SceneRenderer.Color(g.Color).G, SceneRenderer.Color(g.Color).B), 1.5) { DashStyle = XDashStyle.Dash };
                if (g.Summary) { gfx.DrawLine(pen, r.Right, r.Y, r.Right + 8, r.Y); gfx.DrawLine(pen, r.Right + 8, r.Y, r.Right + 8, r.Bottom); gfx.DrawLine(pen, r.Right + 8, r.Bottom, r.Right, r.Bottom); }
                else gfx.DrawRoundedRectangle(pen, r.X, r.Y, r.Width, r.Height, 24, 24);
                Text(g.Text, r.X + 8, r.Y - 22, "Microsoft YaHei UI", 12, false, false, g.Color);
            }
            foreach (var e in scene.Edges)
            {
                var curve = new XGraphicsPath(); curve.AddBezier(e.Start.X, e.Start.Y, e.Control1.X, e.Control1.Y, e.Control2.X, e.Control2.Y, e.End.X, e.End.Y);
                var c = SceneRenderer.Color(e.Color); var pen = new XPen(XColor.FromArgb(c.R, c.G, c.B), e.Dashed ? 1.5 : 2) { DashStyle = e.Dashed ? XDashStyle.Dash : XDashStyle.Solid };
                gfx.DrawPath(pen, curve);
                if (e.Dashed && Arrow(e) is { } arrow) { gfx.DrawLine(pen, arrow.A.X, arrow.A.Y, e.End.X, e.End.Y); gfx.DrawLine(pen, arrow.B.X, arrow.B.Y, e.End.X, e.End.Y); }
                var middle = SceneRenderer.CurveMiddle(e); Text(e.Text, middle.X, middle.Y - 20, "Microsoft YaHei UI", 12, false, false, e.Color);
            }
            foreach (var node in scene.Nodes)
            {
                var r = node.Bounds; var c = SceneRenderer.Color(node.Stroke);
                gfx.DrawRoundedRectangle(new XPen(XColor.FromArgb(c.R, c.G, c.B), node.Topic.Style.StrokeWidth), Brush(node.Fill), r.X, r.Y, r.Width, r.Height, node.Topic.Style.Radius * 2, node.Topic.Style.Radius * 2);
                if (node.Topic.Image is { } key && document.Assets.TryGetValue(key, out var data))
                {
                    using var image = XImage.FromStream(new MemoryStream(data));
                    var imageRect = SceneRenderer.ImageRect(node, image.PixelWidth, image.PixelHeight); gfx.DrawImage(image, imageRect.X, imageRect.Y, imageRect.Width, imageRect.Height);
                }
                for (var i = 0; i < node.Lines.Count; i++) Text(node.Lines[i], r.X + 18, node.TextTop + i * node.LineHeight, node.FontFamily, node.FontSize, node.Bold, node.Italic, node.Foreground);
                Text(node.Badges, r.X + 16, r.Bottom - 26, "Microsoft YaHei UI", 11, false, false, node.Foreground);
            }
            void Text(string value, double x, double y, string family, double size, bool bold, bool italic, string color)
            {
                if (value.Length == 0) return;
                var key = (value, family, size, bold, italic);
                if (!outlineCache.TryGetValue(key, out var outline)) outlineCache[key] = outline = Outline(device, value, family, size, bold, italic);
                var state = gfx.Save(); gfx.TranslateTransform(x, y); gfx.DrawPath(Brush(color), outline.Pdf); gfx.Restore(state);
            }
        }
        AtomicFile.Write(path, s => pdf.Save(s, false));
    }
    private static XSolidBrush Brush(string color) { var c = SceneRenderer.Color(color); return new(XColor.FromArgb(c.R, c.G, c.B)); }
    private static (PointD A, PointD B)? Arrow(VisualEdge e)
    {
        var delta = SceneRenderer.ToVector(e.End) - SceneRenderer.ToVector(e.Control2); if (delta.LengthSquared() < .001) return null;
        var v = Vector2.Normalize(delta); var p = new Vector2(-v.Y, v.X); var end = SceneRenderer.ToVector(e.End);
        var a = end - v * 9 + p * 4; var bb = end - v * 9 - p * 4; return (new(a.X, a.Y), new(bb.X, bb.Y));
    }
    private static OutlinePath Outline(CanvasDevice device, string text, string family, double size, bool bold, bool italic)
    {
        using var format = SceneRenderer.TextFormat(family, size, bold, italic);
        using var layout = new CanvasTextLayout(device, text, format, 10000, 300);
        using var geometry = CanvasGeometry.CreateText(layout);
        using var simplified = geometry.Simplify(CanvasGeometrySimplification.CubicsAndLines);
        var path = new OutlinePath(); simplified.SendPathTo(path); return path;
    }
    private static string N(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
    private static string P(PointD p) => N(p.X) + " " + N(p.Y);
    private static string C(string value) { var c = SceneRenderer.Color(value); return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
    private sealed class OutlinePath : ICanvasPathReceiver
    {
        private readonly StringBuilder svg = new();
        private Vector2 last;
        public string Svg => svg.ToString();
        public XGraphicsPath Pdf { get; } = new();
        public bool Alternate { get; private set; }
        public void BeginFigure(Vector2 startPoint, CanvasFigureFill figureFill) { last = startPoint; svg.Append($"M{N(last.X)} {N(last.Y)} "); Pdf.StartFigure(); }
        public void AddLine(Vector2 endPoint) { svg.Append($"L{N(endPoint.X)} {N(endPoint.Y)} "); Pdf.AddLine(last.X, last.Y, endPoint.X, endPoint.Y); last = endPoint; }
        public void AddCubicBezier(Vector2 controlPoint1, Vector2 controlPoint2, Vector2 endPoint)
        {
            svg.Append($"C{N(controlPoint1.X)} {N(controlPoint1.Y)} {N(controlPoint2.X)} {N(controlPoint2.Y)} {N(endPoint.X)} {N(endPoint.Y)} ");
            Pdf.AddBezier(last.X, last.Y, controlPoint1.X, controlPoint1.Y, controlPoint2.X, controlPoint2.Y, endPoint.X, endPoint.Y); last = endPoint;
        }
        public void AddQuadraticBezier(Vector2 controlPoint, Vector2 endPoint) => AddCubicBezier(last + (controlPoint - last) * 2 / 3, endPoint + (controlPoint - endPoint) * 2 / 3, endPoint);
        public void AddArc(Vector2 endPoint, float radiusX, float radiusY, float rotationAngle, CanvasSweepDirection sweepDirection, CanvasArcSize arcSize) => throw new InvalidOperationException("文字轮廓必须先转换为贝塞尔路径。");
        public void EndFigure(CanvasFigureLoop figureLoop) { if (figureLoop == CanvasFigureLoop.Closed) { svg.Append("Z "); Pdf.CloseFigure(); } }
        public void SetFilledRegionDetermination(CanvasFilledRegionDetermination filledRegionDetermination) { Alternate = filledRegionDetermination == CanvasFilledRegionDetermination.Alternate; Pdf.FillMode = Alternate ? XFillMode.Alternate : XFillMode.Winding; }
        public void SetSegmentOptions(CanvasFigureSegmentOptions figureSegmentOptions) { }
    }
}
