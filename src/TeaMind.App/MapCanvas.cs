using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TeaMind.Core;
using Windows.System;
using Windows.UI.Core;
using Windows.Storage.Streams;

namespace TeaMind.App;

public sealed class MapCanvas : UserControl
{
    private readonly Grid host = new();
    private readonly CanvasControl canvas = new();
    private readonly Canvas overlay = new();
    private readonly TextBox editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private NativeTextMeasurer? measurer;
    private DocumentSession? session;
    private string? editingId;
    private bool rebuilding;
    private bool fitToViewport = true;
    private CanvasCommandList? sceneDrawing;
    private bool drawingDirty = true;
    private RectD? drawingCoverage;
    private int drawingDetailBand;
    private CanvasDevice? drawingDevice;
    private readonly TaskCompletionSource resourcesReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<double> frameTimes = new();
    private PointD? down;
    private Vector2 originalPan;
    private string? dragId;
    private bool panning;
    private bool dragged;
    private bool space;
    private RectD? marquee;
    private int assetGeneration;
    public MapScene Scene { get; private set; } = new();
    public Dictionary<string, CanvasBitmap> Bitmaps { get; } = [];
    public CanvasDevice Device => drawingDevice ?? CanvasDevice.GetSharedDevice();
    public Task Ready => resourcesReady.Task;
    public double AverageDrawMilliseconds => frameTimes.Count == 0 ? 0 : frameTimes.Average();
    public double Zoom { get; private set; } = 0.85;
    private Vector2 pan;
    public event Action? SelectionChanged;
    public event Action<double>? ZoomChanged;
    public event Action<string>? Error;
    public event Action<VirtualKey, bool, bool>? Shortcut;
    public MapCanvas()
    {
        IsTabStop = true;
        editor.Style = (Style)Application.Current.Resources["NodeTextEditorStyle"];
        editor.SelectionHighlightColor = new SolidColorBrush(SceneRenderer.Color("#35775C"));
        AutomationProperties.SetName(this, "思维导图画布，Tab 添加子主题，Enter 添加同级主题，F2 编辑主题，方向键导航");
        host.Children.Add(canvas); host.Children.Add(overlay); overlay.Children.Add(editor); Content = host;
        canvas.ClearColor = SceneRenderer.Color("#F6F8F5");
        overlay.IsHitTestVisible = false;
        canvas.Draw += Draw;
        canvas.CreateResources += (_, _) => { drawingDevice = canvas.Device; measurer = new(Device); drawingDirty = true; Rebuild(); Fit(); resourcesReady.TrySetResult(); };
        SizeChanged += (_, args) =>
        {
            if (fitToViewport) Fit();
            else pan += new Vector2((float)(args.NewSize.Width - args.PreviousSize.Width) / 2, (float)(args.NewSize.Height - args.PreviousSize.Height) / 2);
            PositionEditor();
            canvas.Invalidate();
        };
        PointerPressed += Pressed; PointerMoved += Moved; PointerReleased += Released;
        PointerCanceled += (_, _) => CancelPointer(); PointerCaptureLost += (_, _) => CancelPointer();
        PointerWheelChanged += Wheel;
        DoubleTapped += (_, args) => { var p = args.GetPosition(this); var node = Scene.HitTest(ToWorld(p.X, p.Y)); if (node != null) { session?.Select(node.Topic.Id); BeginEdit(); } args.Handled = true; };
        RightTapped += RightTap;
        KeyDown += OnKeyDown; KeyUp += (_, args) => { if (args.Key == VirtualKey.Space) space = false; };
        editor.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (editingId == null || editor.FocusState != FocusState.Unfocused) return;
            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (focused != null)
            {
                if (ReferenceEquals(focused, editor)) return;
                focused = VisualTreeHelper.GetParent(focused);
            }
            CommitEdit();
        });
        editor.TextChanged += (_, _) => { if (editingId != null) { Rebuild(); PositionEditor(); } };
        editor.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape) { EndEdit(); Focus(FocusState.Programmatic); args.Handled = true; }
            else if (args.Key == VirtualKey.Enter && !ShiftDown()) { CommitEdit(); Focus(FocusState.Programmatic); args.Handled = true; }
        };
        // Visibility changes between home/editor must not tear down the reusable canvas.
    }
    public void SetSession(DocumentSession value)
    {
        CommitEdit(); session = value; assetGeneration++; Rebuild(); _ = LoadImagesAsync(assetGeneration); Fit();
    }
    public void Rebuild()
    {
        if (session == null || rebuilding) return;
        rebuilding = true;
        try
        {
            IReadOnlyDictionary<string, string>? preview = editingId == null ? null : new Dictionary<string, string> { [editingId] = editor.Text };
            Scene = new MapLayoutEngine().Layout(session.Sheet, measurer, preview);
            drawingDirty = true; canvas.ClearColor = SceneRenderer.Color(Scene.Background); canvas.Invalidate();
        }
        catch (Exception ex) { Error?.Invoke(ex.Message); }
        finally { rebuilding = false; }
    }
    public void Invalidate() => canvas.Invalidate();
    public async Task RefreshImagesAsync() => await LoadImagesAsync(++assetGeneration);
    private async Task LoadImagesAsync(int generation)
    {
        if (session == null) return;
        var assets = session.Document.Assets;
        foreach (var bitmap in Bitmaps.Values) bitmap.Dispose(); Bitmaps.Clear();
        foreach (var key in session.Sheet.AllTopics().Select(t => t.Image).Where(k => k != null).Distinct().Cast<string>().ToList())
        {
            if (!assets.TryGetValue(key, out var bytes)) continue;
            try
            {
                using var stream = new InMemoryRandomAccessStream(); await stream.WriteAsync(bytes.AsBuffer()); stream.Seek(0);
                var bitmap = await CanvasBitmap.LoadAsync(Device, stream);
                if (generation != assetGeneration) { bitmap.Dispose(); return; }
                Bitmaps[key] = bitmap;
            }
            catch (Exception ex) { Error?.Invoke("图片无法读取：" + ex.Message); }
        }
        drawingDirty = true; canvas.Invalidate();
    }
    public void Fit()
    {
        fitToViewport = true;
        if (ActualWidth < 1 || ActualHeight < 1) return;
        var b = Scene.Bounds; Zoom = Math.Clamp(Math.Min((ActualWidth - 80) / b.Width, (ActualHeight - 80) / b.Height), 0.08, 1.3);
        pan = new((float)(ActualWidth / 2 - b.Center.X * Zoom), (float)(ActualHeight / 2 - b.Center.Y * Zoom));
        ZoomChanged?.Invoke(Zoom); canvas.Invalidate();
        PositionEditor();
    }
    public void ChangeZoom(double factor, double? x = null, double? y = null)
    {
        CommitEdit(); var cx = x ?? ActualWidth / 2; var cy = y ?? ActualHeight / 2; var world = ToWorld(cx, cy);
        fitToViewport = false;
        Zoom = Math.Clamp(Zoom * factor, 0.08, 4);
        pan = new((float)(cx - world.X * Zoom), (float)(cy - world.Y * Zoom)); ZoomChanged?.Invoke(Zoom); canvas.Invalidate();
    }
    public void CenterOn(string id)
    {
        var node = Scene.Nodes.FirstOrDefault(n => n.Topic.Id == id); if (node == null) return;
        fitToViewport = false;
        pan = new((float)(ActualWidth / 2 - node.Bounds.Center.X * Zoom), (float)(ActualHeight / 2 - node.Bounds.Center.Y * Zoom)); PositionEditor(); canvas.Invalidate();
    }
    public void BeginEdit()
    {
        if (session == null) return;
        CommitEdit(); var node = Scene.Nodes.FirstOrDefault(n => n.Topic.Id == session.Selected.Id); if (node == null) return;
        if (Zoom < 0.6) { ChangeZoom(0.85 / Zoom); CenterOn(node.Topic.Id); }
        editingId = node.Topic.Id; editor.Text = node.Topic.Text; editor.FontFamily = new FontFamily(node.FontFamily); editor.FontSize = node.FontSize * Zoom;
        editor.Background = new SolidColorBrush(SceneRenderer.Color(node.Fill)); editor.Foreground = new SolidColorBrush(SceneRenderer.Color(node.Foreground));
        editor.FontWeight = new() { Weight = (ushort)(node.Bold ? 700 : 400) };
        editor.FontStyle = node.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
        PositionEditor();
        editor.Visibility = Visibility.Visible; overlay.IsHitTestVisible = true; editor.Focus(FocusState.Programmatic); editor.SelectAll();
        drawingDirty = true; canvas.Invalidate();
    }
    private void PositionEditor()
    {
        if (editingId == null) return;
        var node = Scene.Nodes.FirstOrDefault(n => n.Topic.Id == editingId);
        if (node == null) { EndEdit(); return; }
        editor.FontSize = node.FontSize * Zoom;
        editor.Width = Math.Max(1, (node.Bounds.Width - 34) * Zoom);
        editor.MinHeight = node.LineHeight * node.Lines.Count * Zoom;
        editor.MaxHeight = Math.Max(editor.MinHeight, ActualHeight - 30);
        Canvas.SetLeft(editor, (node.Bounds.X + 18) * Zoom + pan.X);
        Canvas.SetTop(editor, node.TextTop * Zoom + pan.Y);
    }
    public void CommitEdit()
    {
        if (session == null || editingId == null) return;
        var id = editingId; var text = editor.Text; EndEdit(false);
        if (session.Sheet.Find(id) is { } topic && topic.Text != text) session.Execute("编辑文字", _ => topic.Text = text);
        else Rebuild();
    }
    private void EndEdit(bool rebuild = true) { editingId = null; editor.Visibility = Visibility.Collapsed; overlay.IsHitTestVisible = false; drawingDirty = true; if (rebuild) Rebuild(); canvas.Invalidate(); }
    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ds = args.DrawingSession;
        var viewport = new RectD(-pan.X / Zoom, -pan.Y / Zoom, ActualWidth / Zoom, ActualHeight / Zoom);
        var detailBand = Zoom < .34 ? 0 : 1;
        if (drawingDirty || sceneDrawing == null || drawingCoverage == null || drawingDetailBand != detailBand || !drawingCoverage.Value.Contains(new(viewport.X, viewport.Y)) || !drawingCoverage.Value.Contains(new(viewport.Right, viewport.Bottom)))
        {
            sceneDrawing?.Dispose(); sceneDrawing = new CanvasCommandList(sender);
            drawingCoverage = viewport.Inflate(300 / Zoom); drawingDetailBand = detailBand;
            using (var cached = sceneDrawing.CreateDrawingSession()) SceneRenderer.Draw(cached, Scene, Bitmaps, viewport: drawingCoverage, editingId: editingId, detailScale: Zoom);
            drawingDirty = false;
        }
        ds.Transform = Matrix3x2.CreateScale((float)Zoom) * Matrix3x2.CreateTranslation(pan);
        ds.DrawImage(sceneDrawing);
        if (session != null)
            foreach (var node in Scene.Nodes.Where(n => session.SelectedIds.Contains(n.Topic.Id) && n.Topic.Id != editingId))
                ds.DrawRoundedRectangle(SceneRenderer.ToRect(node.Bounds.Inflate(4)), 12, 12, SceneRenderer.Color("#4E9B78"), 2);
        if (dragged && dragId != null && down.HasValue)
        {
            var node = Scene.Nodes.FirstOrDefault(n => n.Topic.Id == dragId);
            if (node != null) ds.DrawRoundedRectangle(SceneRenderer.ToRect(node.Bounds.Inflate(8)), 12, 12, SceneRenderer.Color("#4E9B78"), 2);
        }
        if (marquee.HasValue)
        {
            var c = SceneRenderer.Color("#4E9B78"); c.A = 30; ds.FillRectangle(SceneRenderer.ToRect(marquee.Value), c);
            ds.DrawRectangle(SceneRenderer.ToRect(marquee.Value), SceneRenderer.Color("#4E9B78"), 1);
        }
        watch.Stop(); frameTimes.Enqueue(watch.Elapsed.TotalMilliseconds); if (frameTimes.Count > 120) frameTimes.Dequeue();
    }
    private void Pressed(object sender, PointerRoutedEventArgs args)
    {
        if (session == null) return;
        var point = args.GetCurrentPoint(this);
        if (IsInsideEditor(point.Position.X, point.Position.Y)) return;
        if (point.Properties.IsRightButtonPressed) return;
        if (BeginCanvasPress(new(point.Position.X, point.Position.Y), point.Properties.IsMiddleButtonPressed, CtrlDown())) CapturePointer(args.Pointer);
        args.Handled = true;
    }
    private bool BeginCanvasPress(PointD point, bool middleButton, bool toggleSelection)
    {
        if (session == null || IsInsideEditor(point.X, point.Y)) return false;
        CommitEdit(); Focus(FocusState.Pointer);
        down = point; originalPan = pan; dragged = false;
        panning = middleButton || space;
        var world = ToWorld(point.X, point.Y);
        if (!panning)
        {
            var fold = Scene.Nodes.LastOrDefault(n => n.Topic.Children.Count > 0 && Math.Abs(world.X - n.Bounds.Right - 9) < 10 && Math.Abs(world.Y - n.Bounds.Center.Y) < 10);
            if (fold != null) { session.Execute("折叠分支", _ => fold.Topic.Collapsed = !fold.Topic.Collapsed); down = null; return false; }
            var hit = Scene.HitTest(world); dragId = hit?.Topic.Id;
            if (hit != null) session.Select(hit.Topic.Id, toggleSelection);
            else if (!toggleSelection) session.SelectedIds.Clear();
            SelectionChanged?.Invoke(); canvas.Invalidate();
        }
        return true;
    }
    private void Moved(object sender, PointerRoutedEventArgs args)
    {
        if (!down.HasValue || session == null) return;
        var point = args.GetCurrentPoint(this).Position;
        var delta = new Vector2((float)(point.X - down.Value.X), (float)(point.Y - down.Value.Y));
        if (delta.Length() > 6) dragged = true;
        if (panning) { fitToViewport = false; pan = originalPan + delta; }
        else if (dragged && dragId == null)
        {
            var a = ToWorld(down.Value.X, down.Value.Y); var b = ToWorld(point.X, point.Y);
            marquee = new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }
        canvas.Invalidate(); args.Handled = true;
    }
    private void Released(object sender, PointerRoutedEventArgs args)
    {
        if (session == null) return;
        var point = args.GetCurrentPoint(this).Position; var world = ToWorld(point.X, point.Y);
        try
        {
            if (dragged && !panning && dragId != null)
            {
                var hit = Scene.HitTest(world);
                if (hit != null && hit.Topic.Id != dragId)
                {
                    if (ShiftDown() && session.Sheet.ParentOf(hit.Topic.Id) is { } parent)
                        session.Move(dragId, parent.Id, parent.Children.IndexOf(hit.Topic) + (world.Y > hit.Bounds.Center.Y ? 1 : 0));
                    else session.Move(dragId, hit.Topic.Id);
                }
                else if (session.Sheet.FloatingTopics.FirstOrDefault(t => t.Id == dragId) is { } floating)
                    session.Execute("移动自由主题", _ => { floating.X = world.X; floating.Y = world.Y; });
            }
            if (marquee.HasValue)
            {
                foreach (var node in Scene.Nodes.Where(n => n.Bounds.Intersects(marquee.Value))) session.SelectedIds.Add(node.Topic.Id);
                SelectionChanged?.Invoke();
            }
        }
        catch (Exception ex) { Error?.Invoke(ex.Message); }
        CancelPointer(); ReleasePointerCapture(args.Pointer); args.Handled = true;
    }
    private void CancelPointer() { down = null; dragId = null; dragged = false; panning = false; marquee = null; canvas.Invalidate(); }
    private void Wheel(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(this); ChangeZoom(Math.Pow(1.0015, point.Properties.MouseWheelDelta), point.Position.X, point.Position.Y); args.Handled = true;
    }
    private void RightTap(object sender, RightTappedRoutedEventArgs args)
    {
        if (session == null) return;
        var p = args.GetPosition(this); if (IsInsideEditor(p.X, p.Y)) return;
        CommitEdit();
        var hit = Scene.HitTest(ToWorld(p.X, p.Y)); if (hit != null) session.Select(hit.Topic.Id);
        SelectionChanged?.Invoke(); canvas.Invalidate(); var menu = new MenuFlyout();
        Add("编辑文字", () => BeginEdit()); Add("添加子主题", () => { session.Add(false); SelectionChanged?.Invoke(); BeginEdit(); });
        Add("添加同级主题", () => { session.Add(true); SelectionChanged?.Invoke(); BeginEdit(); });
        Add("展开 / 折叠", () => session.Execute("折叠分支", _ => session.Selected.Collapsed = !session.Selected.Collapsed));
        Add("删除", () => { session.DeleteSelected(); SelectionChanged?.Invoke(); });
        menu.ShowAt(this, p); args.Handled = true;
        void Add(string text, Action action) { var item = new MenuFlyoutItem { Text = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    }
    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (editingId != null || session == null) return;
        var ctrl = CtrlDown(); var shift = ShiftDown();
        if (args.Key == VirtualKey.Space) { space = true; args.Handled = true; return; }
        if (ctrl) { Shortcut?.Invoke(args.Key, true, shift); args.Handled = true; return; }
        switch (args.Key)
        {
            case VirtualKey.Tab: session.Add(false); SelectionChanged?.Invoke(); BeginEdit(); break;
            case VirtualKey.Enter: session.Add(true); SelectionChanged?.Invoke(); BeginEdit(); break;
            case VirtualKey.F2: BeginEdit(); break;
            case VirtualKey.Delete: session.DeleteSelected(); SelectionChanged?.Invoke(); break;
            case VirtualKey.Home: session.Select(session.Sheet.Root.Id); CenterOn(session.Sheet.Root.Id); SelectionChanged?.Invoke(); break;
            case VirtualKey.Left: case VirtualKey.Right: case VirtualKey.Up: case VirtualKey.Down: Navigate(args.Key); break;
            default: return;
        }
        args.Handled = true;
    }
    private void Navigate(VirtualKey key)
    {
        if (session == null) return;
        var selected = session.Selected;
        Topic? target;
        if (key == VirtualKey.Left) target = session.Sheet.ParentOf(selected.Id);
        else if (key == VirtualKey.Right)
        {
            if (selected.Collapsed) session.Execute("展开分支", _ => selected.Collapsed = false);
            target = selected.Children.FirstOrDefault();
        }
        else
        {
            var siblings = session.Sheet.ParentOf(selected.Id)?.Children;
            target = siblings == null ? null : siblings.ElementAtOrDefault(siblings.IndexOf(selected) + (key == VirtualKey.Up ? -1 : 1));
        }
        if (target == null) return;
        session.Select(target.Id); CenterOn(target.Id); SelectionChanged?.Invoke();
    }
    public static bool CtrlDown() => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
    public static bool ShiftDown() => (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
    private PointD ToWorld(double x, double y) => new((x - pan.X) / Zoom, (y - pan.Y) / Zoom);
    private bool IsInsideEditor(double x, double y) => editingId != null && new RectD(Canvas.GetLeft(editor), Canvas.GetTop(editor), editor.ActualWidth, editor.ActualHeight).Contains(new(x, y));
    internal void ClickNodeForValidation(string id)
    {
        var node = Scene.Nodes.First(n => n.Topic.Id == id);
        BeginCanvasPress(new(node.Bounds.Center.X * Zoom + pan.X, node.Bounds.Center.Y * Zoom + pan.Y), false, false);
        CancelPointer();
    }
    internal async Task SetEditorTextForValidationAsync(string text)
    {
        if (editor.Text == text) return;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object sender, TextChangedEventArgs args) => changed.TrySetResult();
        editor.TextChanged += Handler;
        try { editor.Text = text; await changed.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { editor.TextChanged -= Handler; }
    }
    internal object InspectEditorForValidation()
    {
        var node = Scene.Nodes.First(n => n.Topic.Id == editingId);
        return new
        {
            zoom = Zoom, editingId, editor.Text,
            expectedX = (node.Bounds.X + 18) * Zoom + pan.X,
            actualX = Canvas.GetLeft(editor),
            expectedY = node.TextTop * Zoom + pan.Y,
            actualY = Canvas.GetTop(editor),
            expectedWidth = (node.Bounds.Width - 34) * Zoom, actualWidth = editor.Width,
            border = editor.BorderThickness.Left,
            background = ((SolidColorBrush)editor.Background).Color.ToString(),
            foreground = ((SolidColorBrush)editor.Foreground).Color.ToString()
        };
    }
    public object BenchmarkDrawing(int count)
    {
        var sheet = new MapSheet();
        for (var i = 0; i < count / 10; i++)
        {
            var branch = new Topic { Text = "测试分支 " + i }; sheet.Root.Children.Add(branch);
            for (var j = 0; j < 9; j++) branch.Children.Add(new() { Text = "中文绘制 " + j });
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var scene = new MapLayoutEngine().Layout(sheet, measurer);
        var layoutMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        using var drawing = new CanvasCommandList(Device);
        var viewport = new RectD(-640 / .75, -360 / .75, 1280 / .75, 720 / .75).Inflate(300 / .75);
        using (var ds = drawing.CreateDrawingSession()) SceneRenderer.Draw(ds, scene, Bitmaps, viewport: viewport, detailScale: .75);
        var recordMs = watch.Elapsed.TotalMilliseconds;
        using var target = new CanvasRenderTarget(Device, 1280, 720, 96);
        var samples = new List<double>();
        for (var frame = 0; frame < 30; frame++)
        {
            watch.Restart();
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(SceneRenderer.Color(scene.Background));
                ds.Transform = Matrix3x2.CreateScale(.75f) * Matrix3x2.CreateTranslation(640 + frame, 360);
                ds.DrawImage(drawing);
            }
            if (frame > 4) samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        return new { nodes = scene.Nodes.Count, layoutMs, recordMs, averageSubmissionMs = samples.Average(), maxSubmissionMs = samples.Max(), note = "离屏缓存绘制的 CPU 提交耗时，不代表显示器帧率或完整交互帧率。" };
    }
}
