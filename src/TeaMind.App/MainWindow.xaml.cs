using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Security.Cryptography;
using System.Text;
using TeaMind.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace TeaMind.App;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings settings = AppSettings.Load();
    private readonly List<DocumentSession> sessions = [];
    private readonly NativeDocumentStore nativeStore = new();
    private readonly XmindConverter xmind = new();
    private readonly RecoveryStore recovery = new(Path.Combine(AppSettings.DataDirectory, "Recovery"));
    private readonly DispatcherTimer recoveryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private DocumentSession active = null!;
    private bool refreshing;
    private bool closingAllowed;
    private bool closePromptOpen;
    private bool snapshotBusy;
    private string inspectorTopicId = "";
    private CancellationTokenSource? aiCancellation;
    private AiProposal? aiProposal;
    private DocumentSession? aiOwner;
    private string? aiSheetId;
    private string? aiTopicId;
    private readonly nint hwnd;
    private string homeCategory = "recent";
    private string documentTabSignature = "";
    private string sheetTabSignature = "";
    private readonly Dictionary<string, long> recoveryRevisions = [];
    public MainWindow()
    {
        InitializeComponent(); Title = "teaMind";
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true; SetTitleBar(WindowDragRegion);
        // RootGrid is opaque. A backdrop behind it adds composition work without a visible effect.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 940));
        AppWindow.Closing += Window_Closing;
        foreach (var theme in ThemePalette.All) ThemeBox.Items.Add(new ComboBoxItem { Content = theme.Name, Tag = theme.Id });
        foreach (var color in new[] { "#356451", "#5A8B70", "#CF9A57", "#8094BB", "#AC82A6", "#FFFFFF", "#30343A" })
        {
            var button = new Button { Width = 26, Height = 26, Padding = new(0), CornerRadius = new(13), Background = new SolidColorBrush(SceneRenderer.Color(color)), BorderThickness = new(1), Tag = color };
            ToolTipService.SetToolTip(button, color); button.Click += (_, _) => ApplyColor(color); ColorSwatches.Children.Add(button);
        }
        MapView.SelectionChanged += RefreshInspector;
        MapView.ZoomChanged += zoom => ZoomLabel.Text = $"{zoom:P0}";
        MapView.Error += message => StatusText.Text = message;
        MapView.Shortcut += CanvasShortcut;
        recoveryTimer.Tick += Recovery_Tick;
        RootGrid.Loaded += Loaded;
        AddAccelerator(VirtualKey.N, VirtualKeyModifiers.Control, () => NewDocument());
        AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control, () => _ = Guard(OpenAsync));
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control, () => _ = Guard(() => SaveAsync(false)));
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _ = Guard(() => SaveAsync(true)));
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, () => { ShowOutline(); SearchBox.Focus(FocusState.Programmatic); SearchBox.SelectAll(); });
        ApplyWindowTheme(); ShowHome();
    }
    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) => { action(); args.Handled = true; }; RootGrid.KeyboardAccelerators.Add(accelerator);
    }
    private async void Loaded(object sender, RoutedEventArgs args)
    {
        RootGrid.Loaded -= Loaded;
        await Guard(async () =>
        {
            var arguments = Environment.GetCommandLineArgs();
            var performanceIndex = Array.IndexOf(arguments, "--window-perf-test");
            if (performanceIndex >= 0 && arguments.Length > performanceIndex + 1)
            {
                await WindowPerformanceTestAsync(arguments[performanceIndex + 1]);
                if (arguments.Contains("--close-after-test")) { closingAllowed = true; Close(); }
                return;
            }
            var regressionIndex = Array.IndexOf(arguments, "--window-regression-test");
            if (regressionIndex >= 0 && arguments.Length > regressionIndex + 1)
            {
                await WindowRegressionTestAsync(arguments[regressionIndex + 1]);
                if (arguments.Contains("--close-after-test")) { closingAllowed = true; Close(); }
                return;
            }
            var smokeIndex = Array.IndexOf(arguments, "--smoke-test");
            if (smokeIndex >= 0 && arguments.Length > smokeIndex + 1)
            {
                var directory = arguments[smokeIndex + 1]; Directory.CreateDirectory(directory);
                await CaptureViewAsync(Path.Combine(directory, "home.png"));
                AddSession(new(MapDocument.CreateWelcome())); await MapView.Ready; MapView.Fit(); await SmokeTestAsync(directory);
                await CaptureViewAsync(Path.Combine(directory, "editor.png"));
                ShowHome(); await Task.Delay(300); await CaptureViewAsync(Path.Combine(directory, "home-with-recent.png"));
                if (arguments.Contains("--close-after-test")) { closingAllowed = true; Close(); }
                return;
            }
            var file = arguments.Skip(1).FirstOrDefault(a => a.EndsWith(".teamind", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".xmind", StringComparison.OrdinalIgnoreCase));
            if (file != null && File.Exists(file)) await OpenPathAsync(file);
            var snapshots = recovery.List();
            if (snapshots.Count == 0) return;
            var dialog = new ContentDialog { Title = "找回上次的想法", Content = $"发现 {snapshots.Count} 份未正常关闭的恢复快照。是否恢复？", PrimaryButtonText = "恢复", SecondaryButtonText = "删除快照", CloseButtonText = "稍后", XamlRoot = RootGrid.XamlRoot };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                foreach (var snapshot in snapshots) { var session = new DocumentSession(recovery.Read(snapshot), true) { FilePath = snapshot.OriginalPath }; AddSession(session); recovery.Save(session); recovery.Remove(snapshot.RecoveryId); }
            else if (result == ContentDialogResult.Secondary) foreach (var snapshot in snapshots) recovery.Remove(snapshot.RecoveryId);
        });
    }
    private void AddSession(DocumentSession session)
    {
        MapView.CommitEdit(); sessions.Add(session); session.Changed += () => SessionChanged(session); SwitchSession(session);
    }
    private void SwitchSession(DocumentSession session)
    {
        MapView.CommitEdit(); active = session; inspectorTopicId = "";
        HomeView.Visibility = Visibility.Collapsed; EditorView.Visibility = Visibility.Visible;
        MapView.SetSession(session); RefreshAll(); MapView.Focus(FocusState.Programmatic);
    }
    private void SessionChanged(DocumentSession session)
    {
        if (session == active) { MapView.Rebuild(); RefreshAll(); }
        else RefreshDocumentTabs();
        recoveryTimer.Stop(); recoveryTimer.Start();
    }
    private void RefreshAll()
    {
        RefreshDocumentTabs(); RefreshSheetTabs(); RefreshOutline(); RefreshInspector();
        UndoButton.IsEnabled = active.CanUndo; RedoButton.IsEnabled = active.CanRedo;
        Title = $"{active.Document.Title}{(active.IsDirty ? " *" : "")} — teaMind";
        DocumentTitle.Text = active.Document.Title; DocumentSaveState.Text = active.IsDirty ? "未保存" : "已保存";
        StatusText.Text = $"{active.Sheet.AllTopics().Count()} 个主题 · {(active.FilePath == null ? "尚未保存" : Path.GetFileName(active.FilePath))}{(active.IsDirty ? " · 未保存" : "")}";
    }
    private void RefreshDocumentTabs()
    {
        var signature = string.Join("|", sessions.Select(s => s.RecoveryId + s.Document.Title + s.IsDirty + (s == active))) + string.Join("|", settings.RecentFiles);
        if (signature == documentTabSignature) return;
        documentTabSignature = signature;
        DocumentTabsRow.Height = new(sessions.Count > 1 ? 32 : 0);
        DocumentTabs.Children.Clear();
        foreach (var session in sessions)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            var button = new Button { Content = session.Document.Title + (session.IsDirty ? " •" : ""), MaxWidth = 230, FontSize = 12, BorderThickness = new(0), Background = session == active ? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] : new SolidColorBrush(Colors.Transparent) };
            button.Click += (_, _) => SwitchSession(session);
            var close = new Button { Content = "×", Padding = new(6, 2, 6, 2), BorderThickness = new(0), Background = new SolidColorBrush(Colors.Transparent) };
            close.Click += async (_, _) => await Guard(async () => { if (await ConfirmCloseAsync(session)) { sessions.Remove(session); recovery.Remove(session.RecoveryId); if (sessions.Count == 0) { active = null!; ShowHome(); } else SwitchSession(sessions.Last()); } });
            row.Children.Add(button); row.Children.Add(close); DocumentTabs.Children.Add(row);
        }
        RecentMenu.Items.Clear();
        foreach (var path in settings.RecentFiles)
        {
            var item = new MenuFlyoutItem { Text = Path.GetFileName(path) }; ToolTipService.SetToolTip(item, path);
            item.Click += async (_, _) => await Guard(() => OpenPathAsync(path)); RecentMenu.Items.Add(item);
        }
    }
    private void RefreshSheetTabs()
    {
        var signature = active.RecoveryId + active.SheetIndex + string.Join("|", active.Document.Sheets.Select(s => s.Id + s.Title));
        if (signature == sheetTabSignature) return;
        sheetTabSignature = signature;
        SheetTabs.Children.Clear();
        for (var i = 0; i < active.Document.Sheets.Count; i++)
        {
            var index = i;
            var button = new Button { Content = active.Document.Sheets[i].Title, FontSize = 12, Padding = new(12, 5, 12, 5), BorderThickness = new(0), Background = i == active.SheetIndex ? new SolidColorBrush(SceneRenderer.Color("#356451")) : new SolidColorBrush(Colors.Transparent), Foreground = i == active.SheetIndex ? new SolidColorBrush(Colors.White) : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] };
            button.Click += (_, _) => { MapView.CommitEdit(); active.SheetIndex = index; active.Select(active.Sheet.Root.Id); MapView.SetSession(active); RefreshAll(); };
            var flyout = new MenuFlyout(); var rename = new MenuFlyoutItem { Text = "重命名画布" };
            rename.Click += async (_, _) => await Guard(async () => { var title = await AskText("画布名称", active.Document.Sheets[index].Title); if (title != null) active.Execute("重命名画布", d => d.Sheets[index].Title = title); }); flyout.Items.Add(rename);
            var remove = new MenuFlyoutItem { Text = "删除画布", IsEnabled = active.Document.Sheets.Count > 1 };
            remove.Click += (_, _) => { active.Execute("删除画布", d => { d.Sheets.RemoveAt(index); active.SheetIndex = Math.Clamp(active.SheetIndex, 0, d.Sheets.Count - 1); }); active.Select(active.Sheet.Root.Id); MapView.Rebuild(); RefreshAll(); }; flyout.Items.Add(remove); button.ContextFlyout = flyout;
            SheetTabs.Children.Add(button);
        }
        var add = new Button { Content = "＋", BorderThickness = new(0), Background = new SolidColorBrush(Colors.Transparent) };
        add.Click += (_, _) => { active.Execute("新建画布", d => { d.Sheets.Add(new() { Title = $"画布 {d.Sheets.Count + 1}" }); active.SheetIndex = d.Sheets.Count - 1; }); active.Select(active.Sheet.Root.Id); MapView.SetSession(active); RefreshAll(); }; SheetTabs.Children.Add(add);
    }
    private void RefreshOutline()
    {
        if (active == null || OutlinePanel.Visibility != Visibility.Visible) return;
        OutlineItems.Children.Clear(); var query = SearchBox.Text.Trim(); var count = 0;
        foreach (var topic in new[] { active.Sheet.Root }.Concat(active.Sheet.FloatingTopics)) Add(topic, 0);
        void Add(Topic topic, int depth)
        {
            if (count >= 1500) return;
            if (query.Length == 0 || topic.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase) || topic.Notes.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                count++; var button = new Button { Content = (topic.Collapsed && topic.Children.Count > 0 ? "▸ " : "") + topic.Text.Replace('\n', ' '), Margin = new(Math.Min(depth, 8) * 12, 0, 0, 0), Padding = new(6, 4, 6, 4), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new(0), Background = new SolidColorBrush(Colors.Transparent) };
                ToolTipService.SetToolTip(button, topic.Text);
                button.Click += (_, _) => { Reveal(topic.Id); active.Select(topic.Id); MapView.CenterOn(topic.Id); RefreshInspector(); MapView.Focus(FocusState.Programmatic); };
                OutlineItems.Children.Add(button);
            }
            if (!topic.Collapsed || query.Length > 0) foreach (var child in topic.Children) Add(child, depth + 1);
        }
    }
    private void Reveal(string id)
    {
        var ancestors = new List<Topic>(); var parent = active.Sheet.ParentOf(id);
        while (parent != null) { if (parent.Collapsed) ancestors.Add(parent); parent = active.Sheet.ParentOf(parent.Id); }
        if (ancestors.Count > 0) active.Execute("展开定位路径", _ => ancestors.ForEach(t => t.Collapsed = false));
    }
    private void RefreshInspector()
    {
        if (active == null) return;
        MapView.Invalidate();
        if (InspectorPanel.Visibility != Visibility.Visible || StylePanel.Visibility != Visibility.Visible) return;
        refreshing = true;
        try
        {
            var topic = active.Selected; inspectorTopicId = topic.Id;
            LayoutBox.SelectedIndex = (int)active.Sheet.Layout;
            ThemeBox.SelectedIndex = Array.FindIndex(ThemePalette.All, t => t.Id == active.Sheet.Theme);
            TopicTextBox.Text = topic.Text; FontSizeBox.Value = topic.Style.FontSize ?? (topic == active.Sheet.Root ? 23 : 15);
            FontFamilyBox.Text = topic.Style.FontFamily ?? "Microsoft YaHei UI";
            BoldButton.IsChecked = topic.Style.Bold; ItalicButton.IsChecked = topic.Style.Italic;
            TagsBox.Text = string.Join(", ", topic.Tags); MarkersBox.Text = string.Join(", ", topic.Markers); NotesBox.Text = topic.Notes; LinkBox.Text = topic.Link;
            ColorHexBox.Text = ""; RemoveImageButton.IsEnabled = topic.Image != null;
            AttachmentItems.Children.Clear();
            foreach (var key in topic.Attachments)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                var save = new Button { Content = "保存 " + Path.GetFileName(key), MaxWidth = 195, FontSize = 11 };
                save.Click += async (_, _) => await Guard(async () =>
                {
                    var destination = await PickSave("附件", Path.GetExtension(key), Path.GetFileNameWithoutExtension(key));
                    if (destination != null) AtomicFile.Write(destination, stream => stream.Write(active.Document.Assets[key]));
                });
                var remove = new Button { Content = "×", Padding = new(6, 2, 6, 2) }; remove.Click += (_, _) => ChangeTopic("移除附件", t => t.Attachments.Remove(key));
                row.Children.Add(save); row.Children.Add(remove); AttachmentItems.Children.Add(row);
            }
            DecorationItems.Children.Clear();
            foreach (var r in active.Sheet.Relationships.Where(r => r.From == topic.Id || r.To == topic.Id).ToList()) AddDecoration(r.Text, title => active.Execute("编辑关系线", _ => r.Text = title), () => active.Execute("删除关系线", _ => active.Sheet.Relationships.Remove(r)));
            foreach (var list in new[] { active.Sheet.Boundaries, active.Sheet.Summaries })
                foreach (var g in list.Where(g => g.ParentId == topic.Id || active.Sheet.ParentOf(topic.Id)?.Id == g.ParentId).ToList()) AddDecoration(g.Text, title => active.Execute("编辑概要或边界", _ => g.Text = title), () => active.Execute("删除概要或边界", _ => list.Remove(g)));
            MapView.Invalidate();
        }
        finally { refreshing = false; }
        void AddDecoration(string text, Action<string> rename, Action remove)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var edit = new Button { Content = text.Length == 0 ? "未命名" : text, MaxWidth = 190, FontSize = 11 };
            edit.Click += async (_, _) => await Guard(async () => { var title = await AskText("名称", text); if (title != null) rename(title); });
            var delete = new Button { Content = "×", Padding = new(6, 2, 6, 2) }; delete.Click += (_, _) => remove(); row.Children.Add(edit); row.Children.Add(delete); DecorationItems.Children.Add(row);
        }
    }
    private void ChangeTopic(string description, Action<Topic> change)
    {
        if (refreshing || active == null) return;
        var id = inspectorTopicId; if (active.Sheet.Find(id) is not { } topic) return;
        active.Execute(description, _ => change(topic));
    }
    private void ApplyColor(string color)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$")) { StatusText.Text = "请输入 #RRGGBB 格式颜色。"; return; }
        var target = ColorTargetBox.SelectedIndex;
        ChangeTopic("修改配色", t => { if (target == 0) t.Style.Fill = color; else if (target == 1) t.Style.Foreground = color; else t.Style.Stroke = color; });
    }
    private void NewDocument() => AddSession(new(new MapDocument(), true));
    private void New_Click(object sender, RoutedEventArgs args) => NewDocument();
    private async void Open_Click(object sender, RoutedEventArgs args) => await Guard(OpenAsync);
    private async Task OpenAsync()
    {
        var picker = new FileOpenPicker(); InitializePicker(picker); picker.FileTypeFilter.Add(".teamind"); picker.FileTypeFilter.Add(".xmind");
        var file = await picker.PickSingleFileAsync(); if (file != null) await OpenPathAsync(file.Path);
    }
    private async Task OpenPathAsync(string path)
    {
        var opened = sessions.FirstOrDefault(s => string.Equals(s.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (opened != null) { SwitchSession(opened); return; }
        var imported = path.EndsWith(".xmind", StringComparison.OrdinalIgnoreCase);
        var result = await Task.Run(() => imported ? xmind.Import(path) : nativeStore.Import(path));
        AddSession(new(result.Document, imported) { FilePath = imported ? null : path }); settings.AddRecent(path);
        if (result.Warnings.Count > 0) await Message("导入兼容性提示", string.Join("\n\n", result.Warnings));
    }
    private async void Save_Click(object sender, RoutedEventArgs args) => await Guard(() => SaveAsync(false));
    private async void SaveAs_Click(object sender, RoutedEventArgs args) => await Guard(() => SaveAsync(true));
    private async Task<bool> SaveAsync(bool saveAs, DocumentSession? owner = null)
    {
        MapView.CommitEdit(); owner ??= active;
        if (owner == null) return false;
        var path = owner.FilePath;
        if (saveAs || path == null) path = await PickSave("teaMind 导图", ".teamind", owner.Document.Title);
        if (path == null) return false;
        // Capture before awaiting; edits made during disk IO must remain marked as unsaved.
        owner.Execute("设置文档名称", d => d.Title = Path.GetFileNameWithoutExtension(path));
        var snapshot = ModelJson.Clone(owner.Document);
        await Task.Run(() => nativeStore.Export(snapshot, path));
        owner.FilePath = path; owner.MarkSavedSnapshot(snapshot); settings.AddRecent(path);
        if (!owner.IsDirty) recovery.Remove(owner.RecoveryId);
        RefreshDocumentTabs(); StatusText.Text = "已保存 · " + Path.GetFileName(path);
        if (owner == active) await CaptureThumbnailAsync(path);
        return true;
    }
    private void AddChild_Click(object sender, RoutedEventArgs args) { MapView.CommitEdit(); active.Add(false); RefreshInspector(); MapView.BeginEdit(); }
    private void AddSibling_Click(object sender, RoutedEventArgs args) { MapView.CommitEdit(); active.Add(true); RefreshInspector(); MapView.BeginEdit(); }
    private void Undo_Click(object sender, RoutedEventArgs args) { MapView.CommitEdit(); active.Undo(); }
    private void Redo_Click(object sender, RoutedEventArgs args) { MapView.CommitEdit(); active.Redo(); }
    private void Collapse_Click(object sender, RoutedEventArgs args) => ChangeTopic("折叠分支", t => t.Collapsed = !t.Collapsed);
    private void ZoomIn_Click(object sender, RoutedEventArgs args) => MapView.ChangeZoom(1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs args) => MapView.ChangeZoom(1 / 1.2);
    private void Fit_Click(object sender, RoutedEventArgs args) => MapView.Fit();
    private void Outline_Click(object sender, RoutedEventArgs args)
    {
        var show = OutlinePanel.Visibility != Visibility.Visible; OutlinePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed; OutlineColumn.Width = new(show ? 220 : 0); if (show) RefreshOutline();
    }
    private void StyleToggle_Click(object sender, RoutedEventArgs args)
    {
        var show = InspectorPanel.Visibility != Visibility.Visible || StylePanel.Visibility != Visibility.Visible;
        InspectorPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed; InspectorColumn.Width = new(show ? 272 : 0);
        StylePanel.Visibility = Visibility.Visible; AiPanel.Visibility = Visibility.Collapsed; if (show) RefreshInspector();
    }
    private void Focus_Click(object sender, RoutedEventArgs args)
    {
        MapView.CommitEdit(); OutlinePanel.Visibility = Visibility.Collapsed; OutlineColumn.Width = new(0);
        InspectorPanel.Visibility = Visibility.Collapsed; InspectorColumn.Width = new(0); MapView.Focus(FocusState.Programmatic);
    }
    private void ShowOutline() { OutlinePanel.Visibility = Visibility.Visible; OutlineColumn.Width = new(220); RefreshOutline(); }
    private void Search_Changed(object sender, TextChangedEventArgs args) => RefreshOutline();
    private void Layout_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (refreshing || active == null || LayoutBox.SelectedIndex < 0) return;
        var layout = (MapLayout)LayoutBox.SelectedIndex; active.Execute("更改布局", _ => active.Sheet.Layout = layout); MapView.Fit();
    }
    private void Theme_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (refreshing || active == null || ThemeBox.SelectedItem is not ComboBoxItem item) return;
        active.Execute("更改主题", _ => active.Sheet.Theme = (string)item.Tag);
    }
    private void Rebalance_Click(object sender, RoutedEventArgs args)
    {
        MapView.CommitEdit();
        active.Execute("均衡左右分支", _ => MindMapBalancer.Rebalance(active.Sheet)); MapView.Fit();
    }
    private void TopicText_LostFocus(object sender, RoutedEventArgs args) { var value = TopicTextBox.Text; ChangeTopic("修改文字", t => t.Text = value); }
    private void FontFamily_LostFocus(object sender, RoutedEventArgs args) { var value = FontFamilyBox.Text.Trim(); if (value.Length > 0) ChangeTopic("修改字体", t => t.Style.FontFamily = value); }
    private void FontSize_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) { if (double.IsFinite(args.NewValue)) ChangeTopic("修改字号", t => t.Style.FontSize = args.NewValue); }
    private void Bold_Click(object sender, RoutedEventArgs args) { var value = BoldButton.IsChecked == true; ChangeTopic("修改粗体", t => t.Style.Bold = value); }
    private void Italic_Click(object sender, RoutedEventArgs args) { var value = ItalicButton.IsChecked == true; ChangeTopic("修改斜体", t => t.Style.Italic = value); }
    private void ResetStyle_Click(object sender, RoutedEventArgs args) => ChangeTopic("恢复默认样式", t => t.Style = new());
    private void ColorHex_LostFocus(object sender, RoutedEventArgs args) { var value = ColorHexBox.Text.Trim(); if (value.Length > 0) ApplyColor(value); }
    private void Tags_LostFocus(object sender, RoutedEventArgs args) { var tags = Split(TagsBox.Text); ChangeTopic("修改标签", t => t.Tags = tags); }
    private void Markers_LostFocus(object sender, RoutedEventArgs args) { var markers = Split(MarkersBox.Text); ChangeTopic("修改标记", t => t.Markers = markers); }
    private static List<string> Split(string value) => value.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
    private void Notes_LostFocus(object sender, RoutedEventArgs args) { var value = NotesBox.Text; ChangeTopic("修改备注", t => t.Notes = value); }
    private void Link_LostFocus(object sender, RoutedEventArgs args) { var value = LinkBox.Text.Trim(); ChangeTopic("修改链接", t => t.Link = value); }
    private async void OpenLink_Click(object sender, RoutedEventArgs args) => await Guard(async () =>
    {
        if (!Uri.TryCreate(active.Selected.Link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto")) throw new InvalidOperationException("仅支持打开 HTTP、HTTPS 或邮件链接。");
        await Launcher.LaunchUriAsync(uri);
    });
    private async void Image_Click(object sender, RoutedEventArgs args) => await Guard(() => AddAssetAsync(true));
    private async void Attachment_Click(object sender, RoutedEventArgs args) => await Guard(() => AddAssetAsync(false));
    private async Task AddAssetAsync(bool image)
    {
        var owner = active; var sheetId = active.Sheet.Id; var topicId = active.Selected.Id;
        var picker = new FileOpenPicker(); InitializePicker(picker);
        foreach (var extension in image ? new[] { ".png", ".jpg", ".jpeg" } : new[] { "*" }) picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync(); if (file == null) return;
        if (new FileInfo(file.Path).Length > 20 * 1024 * 1024) throw new InvalidOperationException("单个图片或附件限制为 20 MB。");
        var bytes = await File.ReadAllBytesAsync(file.Path); var key = "assets/" + Guid.NewGuid().ToString("N") + "-" + file.Name;
        var topic = owner.Document.Sheets.First(s => s.Id == sheetId).Find(topicId) ?? throw new InvalidOperationException("原主题已被删除。");
        owner.Execute(image ? "插入图片" : "插入附件", d => { d.Assets[key] = bytes; if (image) topic.Image = key; else topic.Attachments.Add(key); });
        await MapView.RefreshImagesAsync();
    }
    private async void RemoveImage_Click(object sender, RoutedEventArgs args) { ChangeTopic("移除图片", t => t.Image = null); await MapView.RefreshImagesAsync(); }
    private void Floating_Click(object sender, RoutedEventArgs args)
    {
        var topic = new Topic { Text = "自由主题", X = MapView.Scene.Bounds.Right + 40, Y = 0 };
        active.Execute("添加自由主题", _ => active.Sheet.FloatingTopics.Add(topic)); active.Select(topic.Id); RefreshInspector(); MapView.CenterOn(topic.Id); MapView.BeginEdit();
    }
    private async void Relationship_Click(object sender, RoutedEventArgs args) => await Guard(async () =>
    {
        var ids = active.SelectedIds.ToArray(); if (ids.Length != 2) throw new InvalidOperationException("按住 Ctrl 选择两个主题，再插入关系线。");
        var text = await AskText("关系线名称", "关系"); if (text == null) return;
        active.Execute("添加关系线", _ => active.Sheet.Relationships.Add(new() { From = ids[0], To = ids[1], Text = text }));
    });
    private async void Boundary_Click(object sender, RoutedEventArgs args) => await Guard(() => AddGroupAsync(false));
    private async void Summary_Click(object sender, RoutedEventArgs args) => await Guard(() => AddGroupAsync(true));
    private async Task AddGroupAsync(bool summary)
    {
        var selected = active.SelectedIds.Select(active.Sheet.Find).OfType<Topic>().ToList();
        if (selected.Count == 0) throw new InvalidOperationException("请先选择主题。");
        var parent = active.Sheet.ParentOf(selected[0].Id);
        if (parent == null || selected.Any(t => active.Sheet.ParentOf(t.Id)?.Id != parent.Id)) throw new InvalidOperationException("请选择同一父主题下的相邻主题。");
        var indices = selected.Select(t => parent.Children.IndexOf(t)).Order().ToList();
        if (indices.Last() - indices[0] + 1 != indices.Count) throw new InvalidOperationException("请选择连续相邻的主题。");
        var title = await AskText(summary ? "概要内容" : "边界名称", summary ? "概要" : "分组"); if (title == null) return;
        active.Execute(summary ? "添加概要" : "添加边界", _ => (summary ? active.Sheet.Summaries : active.Sheet.Boundaries).Add(new() { ParentId = parent.Id, Start = indices[0], End = indices.Last(), Text = title }));
    }
    private async void Report_Click(object sender, RoutedEventArgs args) => await Guard(() => Message("Xmind 兼容性报告", string.Join("\n\n", active.Document.CompatibilityWarnings.Prepend("兼容目标：现代 Xmind JSON 文件。当前尚未完成真实 Xmind 客户端的全面往返认证。特殊布局、主题和扩展可能存在差异。"))));
    private async void Export_Click(object sender, RoutedEventArgs args) => await Guard(async () =>
    {
        MapView.CommitEdit(); var format = (string)((MenuFlyoutItem)sender).Tag;
        var scale = new ComboBox { Header = "PNG 清晰度", SelectedIndex = 1, Items = { "1 倍", "2 倍", "3 倍" } };
        var transparent = new CheckBox { Content = "透明背景（PNG / SVG）" };
        var pages = new CheckBox { Content = "PDF 使用 A4 横向分页" };
        if (format is "png" or "svg" or "pdf")
        {
            var content = new StackPanel { Spacing = 14 }; content.Children.Add(new TextBlock { Text = "导出当前画布，包含折叠状态；不添加水印。", TextWrapping = TextWrapping.Wrap });
            if (format == "png") content.Children.Add(scale);
            if (format is "png" or "svg") content.Children.Add(transparent);
            if (format == "pdf") content.Children.Add(pages);
            var dialog = new ContentDialog { Title = "导出 " + format.ToUpperInvariant(), Content = content, PrimaryButtonText = "选择保存位置", CloseButtonText = "取消", XamlRoot = RootGrid.XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        var path = await PickSave(format.ToUpperInvariant(), "." + format, active.Document.Title); if (path == null) return;
        if (active.FilePath != null && Path.GetFullPath(path).Equals(Path.GetFullPath(active.FilePath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("导出请选择独立文件，避免覆盖原始导图。");
        var exporter = new MapExporter(); IReadOnlyList<string> warnings = [];
        switch (format)
        {
            case "png": await MapView.RefreshImagesAsync(); await exporter.ExportPngAsync(path, MapView.Scene, MapView.Device, MapView.Bitmaps, scale.SelectedIndex + 1, transparent.IsChecked == true); break;
            case "svg": exporter.ExportSvg(path, MapView.Scene, MapView.Device, active.Document, transparent.IsChecked == true); break;
            case "pdf": exporter.ExportPdf(path, MapView.Scene, MapView.Device, active.Document, pages.IsChecked == true); break;
            case "md": AtomicFile.WriteText(path, MarkdownExporter.Export(active.Document)); break;
            case "xmind": warnings = xmind.Export(active.Document, path); break;
        }
        StatusText.Text = "已导出 · " + Path.GetFileName(path);
        if (warnings.Count > 0) await Message("导出完成，请检查兼容性差异", string.Join("\n\n", warnings));
    });
    private void AiPanel_Click(object sender, RoutedEventArgs args) { InspectorPanel.Visibility = Visibility.Visible; InspectorColumn.Width = new(300); StylePanel.Visibility = Visibility.Collapsed; AiPanel.Visibility = Visibility.Visible; }
    private void StylePanel_Click(object sender, RoutedEventArgs args) { AiPanel.Visibility = Visibility.Collapsed; StylePanel.Visibility = Visibility.Visible; }
    private async void AiGenerate_Click(object sender, RoutedEventArgs args)
    {
        aiCancellation?.Cancel(); aiCancellation?.Dispose(); aiCancellation = new(); var token = aiCancellation.Token;
        aiProposal = null; AiApplyButton.IsEnabled = false; AiPreviewBox.Text = ""; AiGenerateButton.IsEnabled = false; AiCancelButton.IsEnabled = true; AiProgress.IsActive = true;
        aiOwner = active; aiSheetId = active.Sheet.Id; aiTopicId = active.Selected.Id; var operation = (AiOperation)AiModeBox.SelectedIndex;
        var input = operation == AiOperation.Generate ? AiInputBox.Text : MarkdownExporter.TopicToMarkdown(active.Selected) + (AiInputBox.Text.Length > 0 ? "\n\n补充要求：" + AiInputBox.Text : "");
        AiStatusText.Text = "正在生成；只发送输入文字和本次选中分支。";
        try
        {
            aiProposal = await new CompatibleAiService(http).ProposeAsync(ModelJson.Clone(settings.Ai), settings.GetKey(), operation, input, token);
            AiPreviewBox.Text = aiProposal.Preview; AiApplyButton.IsEnabled = true; AiStatusText.Text = "预览已生成，确认前不会修改导图。";
        }
        catch (OperationCanceledException) { AiStatusText.Text = "已取消，导图没有更改。"; }
        catch (Exception ex) { AiStatusText.Text = ex.Message; }
        finally { AiGenerateButton.IsEnabled = true; AiCancelButton.IsEnabled = false; AiProgress.IsActive = false; }
    }
    private void AiCancel_Click(object sender, RoutedEventArgs args) => aiCancellation?.Cancel();
    private async void AiApply_Click(object sender, RoutedEventArgs args) => await Guard(async () =>
    {
        if (aiProposal == null || aiOwner == null || !sessions.Contains(aiOwner)) throw new InvalidOperationException("原导图已关闭，请重新生成。");
        var sheet = aiOwner.Document.Sheets.FirstOrDefault(s => s.Id == aiSheetId) ?? throw new InvalidOperationException("原画布已删除，请重新生成。");
        var topic = sheet.Find(aiTopicId ?? "") ?? throw new InvalidOperationException("原主题已删除，请重新生成。");
        var proposal = aiProposal; var owner = aiOwner;
        owner.Execute("应用 AI 结果", d =>
        {
            if (proposal.Operation == AiOperation.Generate) { d.Sheets.Add(new() { Title = proposal.Topic.Text.Length > 20 ? proposal.Topic.Text[..20] : proposal.Topic.Text, Root = DocumentSession.CloneWithNewIds(proposal.Topic) }); owner.SheetIndex = d.Sheets.Count - 1; }
            else if (proposal.Operation == AiOperation.Expand) { topic.Children.AddRange((proposal.Topic.Children.Count > 0 ? proposal.Topic.Children : [proposal.Topic]).Select(DocumentSession.CloneWithNewIds)); topic.Collapsed = false; }
            else { topic.Children.Add(DocumentSession.CloneWithNewIds(proposal.Topic)); topic.Collapsed = false; }
        });
        aiProposal = null; AiApplyButton.IsEnabled = false; AiStatusText.Text = "已应用，可使用 Ctrl+Z 整体撤销。"; SwitchSession(owner); await Task.CompletedTask;
    });
    private async void Settings_Click(object sender, RoutedEventArgs args) => await Guard(async () =>
    {
        var theme = new ComboBox { Header = "应用外观", HorizontalAlignment = HorizontalAlignment.Stretch, Items = { "跟随系统", "浅色", "深色" }, SelectedIndex = settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 } };
        var url = new TextBox { Header = "AI 服务地址（包含 /v1 等服务前缀）", Text = settings.Ai.BaseUrl, PlaceholderText = "https://…/v1" };
        var model = new TextBox { Header = "模型名称", Text = settings.Ai.Model, PlaceholderText = "填写服务提供的模型 ID" };
        var key = new PasswordBox { Header = "API 密钥（本地服务可留空）", Password = settings.GetKey() };
        var content = new StackPanel { Spacing = 14, Width = 420 }; content.Children.Add(theme); content.Children.Add(url); content.Children.Add(model); content.Children.Add(key);
        content.Children.Add(new TextBlock { Text = "密钥仅在当前 Windows 用户下加密保存，不写入导图。AI 请求直连你配置的服务，费用由该服务决定。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .65 });
        var dialog = new ContentDialog { Title = "teaMind 设置", Content = content, PrimaryButtonText = "保存", CloseButtonText = "取消", XamlRoot = RootGrid.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("请输入有效的服务地址。");
        settings.Theme = theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" }; settings.Ai.BaseUrl = url.Text.Trim().TrimEnd('/'); settings.Ai.Model = model.Text.Trim(); settings.SetKey(key.Password); settings.Save(); ApplyWindowTheme();
    });
    private void ApplyWindowTheme() => RootGrid.RequestedTheme = settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    private async void CanvasShortcut(VirtualKey key, bool ctrl, bool shift)
    {
        await Guard(async () =>
        {
            switch (key)
            {
                case VirtualKey.Z: if (shift) active.Redo(); else active.Undo(); break;
                case VirtualKey.Y: active.Redo(); break;
                case VirtualKey.C: CopySelection(); break;
                case VirtualKey.X: CopySelection(); active.DeleteSelected(); break;
                case VirtualKey.V: await PasteAsync(); break;
                case VirtualKey.A: active.SelectedIds.Clear(); foreach (var node in MapView.Scene.Nodes) active.SelectedIds.Add(node.Topic.Id); RefreshInspector(); break;
                case VirtualKey.Number0: MapView.Fit(); break;
                case VirtualKey.S: await SaveAsync(shift); break;
                case VirtualKey.N: NewDocument(); break;
                case VirtualKey.O: await OpenAsync(); break;
                case VirtualKey.F: ShowOutline(); SearchBox.Focus(FocusState.Programmatic); break;
            }
        });
    }
    private void CopySelection()
    {
        var ids = active.SelectedIds;
        var topics = active.Sheet.AllTopics().Where(t => ids.Contains(t.Id) && !HasSelectedAncestor(t)).ToList();
        if (topics.Count == 0) return;
        var assetKeys = topics.SelectMany(t => t.DescendantsAndSelf()).SelectMany(t => t.Attachments.Concat(t.Image == null ? [] : new[] { t.Image })).Distinct().ToHashSet();
        var payload = new ClipboardTopics(topics, active.Document.Assets.Where(e => assetKeys.Contains(e.Key)).ToDictionary());
        var data = new DataPackage(); data.SetData("teaMind.Topics", ModelJson.Serialize(payload)); data.SetText(string.Join("\n", topics.Select(MarkdownExporter.TopicToMarkdown))); Clipboard.SetContent(data);
        bool HasSelectedAncestor(Topic t) { var parent = active.Sheet.ParentOf(t.Id); while (parent != null) { if (ids.Contains(parent.Id)) return true; parent = active.Sheet.ParentOf(parent.Id); } return false; }
    }
    private async Task PasteAsync()
    {
        var content = Clipboard.GetContent();
        if (content.Contains("teaMind.Topics"))
        {
            var text = (string)await content.GetDataAsync("teaMind.Topics"); if (text.Length > 30000000) throw new InvalidOperationException("剪贴板内容过大。");
            var payload = ModelJson.Deserialize<ClipboardTopics>(text);
            active.Execute("粘贴主题", d => { foreach (var (key, bytes) in payload.Assets) d.Assets[key] = bytes; active.Selected.Children.AddRange(payload.Topics.Select(DocumentSession.CloneWithNewIds)); active.Selected.Collapsed = false; });
        }
        else if (content.Contains(StandardDataFormats.Text))
        {
            var text = await content.GetTextAsync(); if (text.Length > 100000) throw new InvalidOperationException("剪贴板文字过长。");
            active.Paste(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(1000).Select(line => new Topic { Text = line.TrimStart('-', ' ', '\t') }));
        }
        RefreshInspector();
    }
    private sealed record ClipboardTopics(List<Topic> Topics, Dictionary<string, byte[]> Assets);
    private async void Recovery_Tick(object? sender, object args)
    {
        recoveryTimer.Stop(); if (snapshotBusy) { recoveryTimer.Start(); return; }
        snapshotBusy = true;
        try
        {
            foreach (var session in sessions.Where(s => s.IsDirty && recoveryRevisions.GetValueOrDefault(s.RecoveryId, -1) != s.Revision).ToList())
            {
                var revision = session.Revision;
                // Capture immutable JSON on the UI thread; compression and disk writes run in the background.
                var json = ModelJson.Serialize(session.Document);
                var id = session.RecoveryId; var originalPath = session.FilePath;
                await Task.Run(() => recovery.SaveSnapshot(id, originalPath, ModelJson.Deserialize<MapDocument>(json)));
                recoveryRevisions[id] = revision;
            }
        }
        catch (Exception ex) { StatusText.Text = "恢复快照写入失败：" + ex.Message; }
        finally { snapshotBusy = false; }
    }
    private async Task<bool> ConfirmCloseAsync(DocumentSession session)
    {
        MapView.CommitEdit(); if (!session.IsDirty) return true;
        var dialog = new ContentDialog { Title = "保存你的想法？", Content = $"“{session.Document.Title}”有尚未保存的更改。", PrimaryButtonText = "保存", SecondaryButtonText = "不保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot };
        return await dialog.ShowAsync() switch { ContentDialogResult.Primary => await SaveAsync(false, session), ContentDialogResult.Secondary => true, _ => false };
    }
    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closingAllowed) return; args.Cancel = true; if (closePromptOpen) return; closePromptOpen = true;
        await Guard(async () =>
        {
            foreach (var session in sessions.ToList()) if (!await ConfirmCloseAsync(session)) return;
            foreach (var session in sessions) recovery.Remove(session.RecoveryId);
            recoveryTimer.Stop(); aiCancellation?.Cancel(); closingAllowed = true; Close();
        });
        closePromptOpen = false;
    }
    private void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    private async Task<string?> PickSave(string description, string extension, string name)
    {
        var picker = new FileSavePicker { SuggestedFileName = string.Join("_", name.Split(Path.GetInvalidFileNameChars())).TrimEnd('.') };
        picker.FileTypeChoices.Add(description, [extension.Length > 1 ? extension : ".bin"]); InitializePicker(picker);
        var file = await picker.PickSaveFileAsync(); return file?.Path;
    }
    private async Task<string?> AskText(string title, string value)
    {
        var box = new TextBox { Text = value, MinWidth = 320, AcceptsReturn = false };
        var dialog = new ContentDialog { Title = title, Content = box, PrimaryButtonText = "确定", CloseButtonText = "取消", XamlRoot = RootGrid.XamlRoot, DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }
    private Task Message(string title, string content)
    {
        var dialog = new ContentDialog { Title = title, Content = new ScrollViewer { MaxHeight = 480, Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } }, CloseButtonText = "知道了", XamlRoot = RootGrid.XamlRoot };
        return dialog.ShowAsync().AsTask();
    }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { StatusText.Text = ex.Message; await Message("操作未完成", ex.Message); }
    }
    public async Task SmokeTestAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            active.Execute("验证中文编辑", _ => active.Sheet.Root.Notes = "中文备注 / emoji 🍵"); active.Undo(); active.Redo();
            nativeStore.Export(active.Document, Path.Combine(directory, "sample.teamind"));
            var native = nativeStore.Import(Path.Combine(directory, "sample.teamind")).Document;
            xmind.Export(native, Path.Combine(directory, "sample.xmind"));
            var converted = xmind.Import(Path.Combine(directory, "sample.xmind")).Document;
            if (converted.Sheets[0].Root.Text != native.Sheets[0].Root.Text) throw new InvalidDataException("Xmind 文本往返失败。");
            var export = new MapExporter(); await MapView.RefreshImagesAsync();
            await export.ExportPngAsync(Path.Combine(directory, "sample.png"), MapView.Scene, MapView.Device, MapView.Bitmaps, 2, false);
            export.ExportSvg(Path.Combine(directory, "sample.svg"), MapView.Scene, MapView.Device, active.Document, false);
            export.ExportPdf(Path.Combine(directory, "sample.pdf"), MapView.Scene, MapView.Device, active.Document, false);
            export.ExportPdf(Path.Combine(directory, "sample-paged.pdf"), MapView.Scene, MapView.Device, active.Document, true);
            AtomicFile.WriteText(Path.Combine(directory, "sample.md"), MarkdownExporter.Export(active.Document));
            settings.AddRecent(Path.Combine(directory, "sample.teamind")); await CaptureThumbnailAsync(Path.Combine(directory, "sample.teamind"));
            var before = ModelJson.Serialize(active.Document); active.Execute("添加测试节点", _ => active.Sheet.Root.Children.Add(new() { Text = "验证节点" })); active.Undo();
            if (ModelJson.Serialize(active.Document) != before) throw new InvalidDataException("撤销验证失败。");
            var rendering = new[] { MapView.BenchmarkDrawing(1000), MapView.BenchmarkDrawing(5000) };
            AtomicFile.WriteText(Path.Combine(directory, "render-performance.json"), ModelJson.Serialize(rendering));
            active.MarkSaved(); AtomicFile.WriteText(Path.Combine(directory, "smoke-result.json"), ModelJson.Serialize(new { success = true, topics = active.Sheet.AllTopics().Count(), time = DateTimeOffset.Now }));
            StatusText.Text = "原生运行验证通过 · 文件 / 撤销 / PNG / SVG / PDF / Xmind";
        }
        catch (Exception ex) { AtomicFile.WriteText(Path.Combine(directory, "smoke-result.json"), ModelJson.Serialize(new { success = false, error = ex.ToString() })); StatusText.Text = "运行验证失败：" + ex.Message; }
    }
    private void Home_Click(object sender, RoutedEventArgs args) => ShowHome();
    private void ShowHome()
    {
        MapView.CommitEdit(); EditorView.Visibility = Visibility.Collapsed; HomeView.Visibility = Visibility.Visible; Title = "teaMind"; RefreshHome();
    }
    private void HomeRecent_Click(object sender, RoutedEventArgs args) { homeCategory = "recent"; RefreshHome(); }
    private void HomeFavorites_Click(object sender, RoutedEventArgs args) { homeCategory = "favorites"; RefreshHome(); }
    private void HomeOpenDocs_Click(object sender, RoutedEventArgs args) { homeCategory = "open"; RefreshHome(); }
    private void HomeSearch_Changed(object sender, TextChangedEventArgs args) => RefreshHome();
    private void HomeAi_Click(object sender, RoutedEventArgs args) { NewDocument(); AiPanel_Click(sender, args); }
    private void RefreshHome()
    {
        if (HomeCards == null) return;
        HomeHeading.Text = homeCategory switch { "favorites" => "收藏", "open" => "当前打开", _ => "最近" };
        HomeDescription.Text = homeCategory switch { "favorites" => "把常用的导图留在手边。", "open" => "回到尚未结束的思考。", _ => "从一个想法开始，或接着上次的思考。" };
        HomeCards.Items.Clear();
        var create = CardContainer("新建导图", "从空白画布开始", null, true); create.Click += (_, _) => NewDocument(); HomeCards.Items.Add(create);
        var query = HomeSearch?.Text.Trim() ?? "";
        if (homeCategory == "open")
        {
            foreach (var session in sessions.Where(s => s.Document.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            {
                var card = CardContainer(session.Document.Title + (session.IsDirty ? " •" : ""), "继续编辑", session.FilePath);
                card.Click += (_, _) => SwitchSession(session); HomeCards.Items.Add(card);
            }
            return;
        }
        foreach (var path in (homeCategory == "favorites" ? settings.Favorites : settings.RecentFiles).Where(p => Path.GetFileNameWithoutExtension(p).Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList())
        {
            var exists = File.Exists(path);
            var card = CardContainer(Path.GetFileNameWithoutExtension(path), exists ? Path.GetExtension(path).TrimStart('.').ToUpperInvariant() + " · 本地文件" : "文件已移动或不存在", path);
            ToolTipService.SetToolTip(card, path); card.Click += async (_, _) => await Guard(() => OpenPathAsync(path));
            var menu = new MenuFlyout();
            var favorite = new MenuFlyoutItem { Text = settings.Favorites.Contains(path) ? "取消收藏" : "收藏" };
            favorite.Click += (_, _) => { if (!settings.Favorites.Remove(path)) settings.Favorites.Add(path); settings.Save(); RefreshHome(); }; menu.Items.Add(favorite);
            var remove = new MenuFlyoutItem { Text = "从最近记录移除" }; remove.Click += (_, _) => { settings.RecentFiles.Remove(path); settings.Save(); RefreshHome(); }; menu.Items.Add(remove);
            card.ContextFlyout = menu; HomeCards.Items.Add(card);
        }
    }
    private Button CardContainer(string title, string subtitle, string? path, bool create = false)
    {
        var stack = new StackPanel { Spacing = 10, Width = 208 };
        var preview = new Border { Height = 132, CornerRadius = new(12), BorderThickness = new(1), BorderBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] };
        if (path != null && File.Exists(ThumbnailPath(path))) preview.Child = new Image { Source = new BitmapImage(new Uri(ThumbnailPath(path))), Stretch = Stretch.Uniform };
        else if (create) preview.Child = new FontIcon { Glyph = "\uE710", FontSize = 36, Opacity = .65 };
        else
        {
            var graphic = new Canvas { Width = 170, Height = 100 };
            void Node(double x, double y, double w, double h, string fill)
            {
                var node = new Border { Width = w, Height = h, CornerRadius = new(4), Background = new SolidColorBrush(SceneRenderer.Color(fill)) };
                Canvas.SetLeft(node, x); Canvas.SetTop(node, y); graphic.Children.Add(node);
            }
            Node(68, 39, 38, 23, "#356451"); Node(18, 15, 30, 13, "#8FB49D"); Node(14, 70, 34, 13, "#C3AE7E"); Node(126, 12, 38, 13, "#91A6C5"); Node(128, 72, 32, 13, "#B99FB8");
            foreach (var end in new[] { (33d, 28d), (31d, 70d), (145d, 25d), (144d, 72d) })
            {
                var line = new Microsoft.UI.Xaml.Shapes.Line { X1 = 87, Y1 = 50, X2 = end.Item1, Y2 = end.Item2, Stroke = new SolidColorBrush(SceneRenderer.Color("#80988A")), StrokeThickness = 1.5 };
                graphic.Children.Insert(0, line);
            }
            preview.Child = graphic;
        }
        stack.Children.Add(preview); stack.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 200 });
        stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 10, Opacity = .45, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, -5, 0, 0) });
        return new Button { Content = stack, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new(0), Padding = new(0), Margin = new(0, 0, 24, 28), VerticalAlignment = VerticalAlignment.Top };
    }
    private static string ThumbnailPath(string path) => Path.Combine(AppSettings.DataDirectory, "Thumbnails", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..24] + ".png");
    private async Task CaptureThumbnailAsync(string path)
    {
        try
        {
            await MapView.Ready;
            var target = ThumbnailPath(path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var b = MapView.Scene.Bounds; var scale = Math.Min(416 / b.Width, 264 / b.Height);
            await new MapExporter().ExportPngAsync(target, MapView.Scene, MapView.Device, MapView.Bitmaps, scale, false);
        }
        catch (Exception ex) { StatusText.Text = "文件已保存，预览图生成失败：" + ex.Message; }
    }
    private async Task CaptureViewAsync(string path)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(RootGrid);
        var pixels = await bitmap.GetPixelsAsync();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        using var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels); var bytes = new byte[pixels.Length]; reader.ReadBytes(bytes);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync(); stream.Seek(0);
        var output = new byte[stream.Size]; using var outputReader = new Windows.Storage.Streams.DataReader(stream); await outputReader.LoadAsync((uint)stream.Size); outputReader.ReadBytes(output);
        AtomicFile.Write(path, target => target.Write(output));
    }
    private async Task WindowRegressionTestAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            RootGrid.RequestedTheme = ElementTheme.Dark;
            AddSession(new(new MapDocument(), true)); await MapView.Ready;
            // Loaded can run before native activation has restored keyboard focus.
            // Exercise editing after the test window is fully active, like a real user.
            Activate(); await Task.Delay(250);
            var child = active.Add(false); MapView.Rebuild();
            var samples = new List<object>();
            foreach (var zoom in new[] { 1d, 1.5, 2d })
            {
                MapView.ChangeZoom(zoom / MapView.Zoom); MapView.CenterOn(child.Id); MapView.BeginEdit();
                await MapView.SetEditorTextForValidationAsync("中文输入测试");
                if (active.Sheet.Find(child.Id)?.Text != "新主题") throw new InvalidOperationException($"编辑预览提前修改了文档，缩放 {zoom}，当前文字：{active.Sheet.Find(child.Id)?.Text}。");
                samples.Add(MapView.InspectEditorForValidation());
                await CaptureViewAsync(Path.Combine(directory, $"editing-{zoom:0.0}.png"));
                MapView.ClickNodeForValidation(active.Sheet.Root.Id);
                if (active.Selected.Id != active.Sheet.Root.Id) throw new InvalidOperationException("编辑子主题后点击中心主题，未切换选择。");
                if (active.Sheet.Find(child.Id)?.Text != "中文输入测试") throw new InvalidOperationException("原生输入框文字提交失败。");
                active.Undo(); active.Select(child.Id); MapView.Rebuild();
            }
            var scale = RootGrid.XamlRoot.RasterizationScale;
            var origin = new NativePoint(); ClientToScreen(hwnd, ref origin);
            var hitTests = new List<object>();
            foreach (var fraction in new[] { .2, .5, .8 })
            {
                var x = origin.X + (int)(WindowDragRegion.ActualWidth * fraction * scale);
                var y = origin.Y + (int)(12 * scale);
                var point = (nint)((y & 0xffff) << 16 | (x & 0xffff));
                var hit = SendMessage(hwnd, 0x84, 0, point).ToInt32();
                hitTests.Add(new { fraction, hit, expected = 2 });
                if (hit != 2) throw new InvalidOperationException($"顶部 {fraction:P0} 处没有返回窗口标题栏命中结果，实际为 {hit}。");
            }
            var editorInfo = new System.Text.Json.Nodes.JsonArray(samples.Select(s => System.Text.Json.JsonSerializer.SerializeToNode(s)).ToArray());
            foreach (var info in editorInfo)
                if (Math.Abs(info!["expectedX"]!.GetValue<double>() - info["actualX"]!.GetValue<double>()) > .01 || Math.Abs(info["expectedY"]!.GetValue<double>() - info["actualY"]!.GetValue<double>()) > .01 || info["border"]!.GetValue<double>() != 0)
                    throw new InvalidOperationException("编辑框位置或边框不符合节点文字区域。");
            var presenter = (OverlappedPresenter)AppWindow.Presenter;
            var centerX = origin.X + (int)(WindowDragRegion.ActualWidth / 2 * scale);
            var centerY = origin.Y + (int)(12 * scale);
            SendMessage(hwnd, 0xA3, 2, (nint)((centerY & 0xffff) << 16 | (centerX & 0xffff)));
            await Task.Delay(100);
            var maximized = presenter.State == OverlappedPresenterState.Maximized;
            if (!maximized) throw new InvalidOperationException("标题栏双击未最大化窗口。");
            presenter.Restore(); await Task.Delay(100);
            var restored = presenter.State == OverlappedPresenterState.Restored;
            AtomicFile.WriteText(Path.Combine(directory, "result.json"), ModelJson.Serialize(new { success = restored, dragWidth = WindowDragRegion.ActualWidth, dragHeight = WindowDragRegion.ActualHeight, hitTests, doubleClickMaximized = maximized, restored, editor = samples }));
        }
        catch (Exception ex) { AtomicFile.WriteText(Path.Combine(directory, "result.json"), ModelJson.Serialize(new { success = false, error = ex.ToString() })); }
    }
    private async Task WindowPerformanceTestAsync(string directory)
    {
        Directory.CreateDirectory(directory); Activate(); await Task.Delay(300);
        var results = new List<object>();
        var original = AppWindow.Position;
        var process = Process.GetCurrentProcess();
        try
        {
            foreach (var nativeTitleBar in new[] { true, false })
            {
                ExtendsContentIntoTitleBar = !nativeTitleBar;
                SetTitleBar(nativeTitleBar ? null : WindowDragRegion);
                SystemBackdrop = null;
                foreach (var editorVisible in new[] { false, true })
                {
                    if (editorVisible)
                    {
                        if (sessions.Count == 0) AddSession(new(MapDocument.CreateWelcome())); else SwitchSession(sessions[0]);
                        await MapView.Ready;
                    }
                    else ShowHome();
                    await Task.Delay(150);
                    var intervals = new List<double>(); var calls = new List<double>(); var watch = Stopwatch.StartNew();
                    var cpuBefore = process.TotalProcessorTime; var last = watch.Elapsed.TotalMilliseconds;
                    for (var i = 0; i < 60; i++)
                    {
                        await Task.Delay(16);
                        var now = watch.Elapsed.TotalMilliseconds; intervals.Add(now - last); last = now;
                        var call = Stopwatch.StartNew();
                        AppWindow.Move(new(original.X + (int)(Math.Sin(i * .2) * 70), original.Y + (int)(Math.Cos(i * .2) * 45)));
                        calls.Add(call.Elapsed.TotalMilliseconds);
                    }
                    var total = watch.Elapsed.TotalMilliseconds;
                    results.Add(new { nativeTitleBar, editorVisible, elapsedMs = total, cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds, averageIntervalMs = intervals.Average(), maxIntervalMs = intervals.Max(), averageMoveCallMs = calls.Average(), maxMoveCallMs = calls.Max(), forceSoftware = MapView.Device.ForceSoftwareRenderer });
                    AppWindow.Move(original);
                }
            }
            AtomicFile.WriteText(Path.Combine(directory, "result.json"), ModelJson.Serialize(new { success = true, results, note = "UI 线程驱动的窗口移动测试；间隔含 16ms 计时等待，不代表端到端输入延迟或显示帧率。" }));
        }
        catch (Exception ex) { AtomicFile.WriteText(Path.Combine(directory, "result.json"), ModelJson.Serialize(new { success = false, error = ex.ToString() })); }
        finally { SystemBackdrop = null; ExtendsContentIntoTitleBar = true; SetTitleBar(WindowDragRegion); AppWindow.Move(original); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
