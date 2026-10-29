using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TeaMind.Core;

var tests = new List<(string Name, Func<Task> Run)>();
var root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "teaMind-tests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
void Check(bool condition, string message = "断言失败") { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("预期异常 " + typeof(T).Name); }
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("预期异常 " + typeof(T).Name); }
MapDocument Sample() => MapDocument.CreateWelcome();
DocumentSession Session() => new(Sample());

Test("新增节点、同级顺序与撤销重做", () =>
{
    var s = Session(); var before = ModelJson.Serialize(s.Document); var child = s.Add(false);
    Check(s.IsDirty && child.Id == s.Selected.Id); s.Undo(); Check(!s.IsDirty && before == ModelJson.Serialize(s.Document));
    s.Redo(); Check(s.Sheet.Find(child.Id) != null); var sibling = s.Add(true); var parent = s.Sheet.ParentOf(child.Id)!;
    Check(parent.Children.IndexOf(sibling) == parent.Children.FindIndex(t => t.Id == child.Id) + 1);
});
Test("禁止移动到自身、后代或移动根节点", () =>
{
    var s = Session(); var child = s.Sheet.Root.Children[0]; var nested = child.Children[0]; var before = ModelJson.Serialize(s.Document);
    Throws<InvalidOperationException>(() => s.Move(child.Id, nested.Id)); Throws<InvalidOperationException>(() => s.Move(child.Id, child.Id));
    Throws<InvalidOperationException>(() => s.Move(s.Sheet.Root.Id, child.Id)); Check(ModelJson.Serialize(s.Document) == before);
});
Test("换父节点与同父节点重排", () =>
{
    var s = Session(); var a = s.Sheet.Root.Children[0]; var b = s.Sheet.Root.Children[1];
    s.Move(a.Id, s.Sheet.Root.Id, 2); Check(s.Sheet.Root.Children[1].Id == a.Id);
    s.Move(a.Id, b.Id); Check(s.Sheet.ParentOf(a.Id)?.Id == b.Id); s.Undo(); Check(s.Sheet.ParentOf(a.Id)?.Id == s.Sheet.Root.Id);
});
Test("删除清理关系线且可撤销", () =>
{
    var d = Sample(); var child = d.Sheets[0].Root.Children[0]; d.Sheets[0].Relationships.Add(new() { From = child.Id, To = d.Sheets[0].Root.Id });
    var s = new DocumentSession(d); s.Select(child.Id); s.DeleteSelected(); Check(s.Sheet.Find(child.Id) == null && s.Sheet.Relationships.Count == 0);
    s.Undo(); Check(s.Sheet.Find(child.Id) != null && s.Sheet.Relationships.Count == 1);
});
Test("复制粘贴分配新 ID，保留文字和备注", () =>
{
    var s = Session(); var old = s.Sheet.Root.Children[0]; var copy = DocumentSession.CloneWithNewIds(old);
    Check(copy.Text == old.Text && !copy.DescendantsAndSelf().Select(t => t.Id).Intersect(old.DescendantsAndSelf().Select(t => t.Id)).Any());
    s.Paste([copy]); DocumentValidator.Validate(s.Document); s.Undo(); Check(!s.IsDirty);
});
Test("无效编辑回滚，不污染撤销状态", () =>
{
    var s = Session(); var before = ModelJson.Serialize(s.Document);
    Throws<InvalidDataException>(() => s.Execute("无效字号", _ => s.Sheet.Root.Style.FontSize = double.NaN));
    Check(before == ModelJson.Serialize(s.Document) && !s.CanUndo && !s.IsDirty);
});
Test("边界随插入删除更新范围", () =>
{
    var d = Sample(); var sheet = d.Sheets[0]; sheet.Boundaries.Add(new() { ParentId = sheet.Root.Id, Start = 1, End = 2 });
    var s = new DocumentSession(d); s.Select(sheet.Root.Children[0].Id); s.Add(true);
    Check(s.Sheet.Boundaries[0].Start == 2 && s.Sheet.Boundaries[0].End == 3);
    s.Select(s.Sheet.Root.Children[0].Id); s.DeleteSelected(); Check(s.Sheet.Boundaries[0].Start == 1 && s.Sheet.Boundaries[0].End == 2);
});
Test("所有布局完整显示所有未折叠节点", () =>
{
    var d = Sample(); var sheet = d.Sheets[0];
    foreach (var layout in Enum.GetValues<MapLayout>())
    {
        sheet.Layout = layout; var scene = new MapLayoutEngine().Layout(sheet);
        Check(scene.Nodes.Count == sheet.AllTopics().Count(), layout.ToString());
        Check(scene.Nodes.All(n => double.IsFinite(n.Bounds.X) && double.IsFinite(n.Bounds.Y) && n.Bounds.Width > 0 && n.Bounds.Height > 0));
    }
});
Test("折叠隐藏后代，保留模型", () =>
{
    var sheet = Sample().Sheets[0]; var branch = sheet.Root.Children[0]; var count = sheet.AllTopics().Count(); branch.Collapsed = true;
    var scene = new MapLayoutEngine().Layout(sheet); Check(scene.Nodes.Count == count - branch.Children.Count); Check(sheet.AllTopics().Count() == count);
});
Test("左右、组织图不发生节点重叠", () =>
{
    var sheet = Sample().Sheets[0];
    foreach (var layout in new[] { MapLayout.MindMap, MapLayout.Right, MapLayout.Left, MapLayout.Organization, MapLayout.Tree })
    {
        sheet.Layout = layout; var nodes = new MapLayoutEngine().Layout(sheet).Nodes;
        for (var i = 0; i < nodes.Count; i++) for (var j = i + 1; j < nodes.Count; j++) Check(!nodes[i].Bounds.Intersects(nodes[j].Bounds), layout + "节点重叠");
    }
});
Test("长文本、表情与自由主题布局", () =>
{
    var sheet = Sample().Sheets[0]; sheet.Root.Text = string.Concat(Enumerable.Repeat("中文🍵verylong", 15)); sheet.FloatingTopics.Add(new() { Text = "自由", X = 800, Y = 900 });
    var scene = new MapLayoutEngine().Layout(sheet); Check(scene.Nodes[0].Lines.Count > 1); Check(scene.Nodes.Last().Bounds.X == 800);
});
Test("原生文件包含中文、多画布、资源与未知字段", () =>
{
    var d = Sample(); d.Sheets.Add(new() { Title = "第二画布" }); d.Assets["assets/test.txt"] = Encoding.UTF8.GetBytes("附件中文");
    d.XmindEntries["resources/original.bin"] = [1, 2, 3]; d.Sheets[0].Root.Attachments.Add("assets/test.txt"); d.Sheets[0].Root.XmindData = new() { ["custom"] = "保留" };
    var file = Path.Combine(root, "roundtrip.teamind"); var store = new NativeDocumentStore(); store.Export(d, file); var imported = store.Import(file).Document;
    Check(ModelJson.Serialize(d) == ModelJson.Serialize(imported));
});
Test("拒绝未来文档版本、重复标识和零画布", () =>
{
    var d = Sample(); d.Version = 2; Throws<InvalidDataException>(() => DocumentValidator.Validate(d)); d.Version = 1;
    d.Sheets[0].Root.Children[0].Id = d.Sheets[0].Root.Id; Throws<InvalidDataException>(() => DocumentValidator.Validate(d));
    d.Sheets.Clear(); Throws<InvalidDataException>(() => DocumentValidator.Validate(d));
});
Test("拒绝路径穿越压缩包与重复文件", () =>
{
    var path = Path.Combine(root, "malicious.zip");
    using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry("../outside.txt");
    Throws<InvalidDataException>(() => SafeArchive.Read(path));
    path = Path.Combine(root, "duplicate.zip"); using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { zip.CreateEntry("test"); zip.CreateEntry("test"); }
    Throws<InvalidDataException>(() => SafeArchive.Read(path));
});
Test("写入失败保持原文件且清理临时文件", () =>
{
    var path = Path.Combine(root, "atomic.txt"); File.WriteAllText(path, "original");
    Throws<IOException>(() => AtomicFile.Write(path, s => { s.WriteByte(1); throw new IOException("模拟磁盘写入失败"); }));
    Check(File.ReadAllText(path) == "original"); Check(!Directory.EnumerateFiles(root, "atomic.txt.*.tmp").Any());
});
Test("恢复快照独立于原文件，可读取和删除", () =>
{
    var recovery = new RecoveryStore(Path.Combine(root, "recovery")); var s = Session(); s.FilePath = Path.Combine(root, "not-overwritten.teamind");
    s.Add(false); recovery.Save(s); var snapshot = recovery.List().Single(); Check(snapshot.OriginalPath == s.FilePath);
    Check(recovery.Read(snapshot).Sheets[0].Root.Text == s.Sheet.Root.Text && !File.Exists(s.FilePath)); recovery.Remove(s.RecoveryId); Check(recovery.List().Count == 0);
});
var fixture = Path.Combine(root, "fixture.xmind");
SafeArchive.Write(fixture, new Dictionary<string, byte[]> {
    ["content.json"] = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "modern-content.json")),
    ["resources/sample.png"] = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="),
    ["resources/manual.txt"] = Encoding.UTF8.GetBytes("手册"), ["custom.bin"] = [7,8,9]
});
Test("现代 Xmind 多画布、折叠、备注、样式和资源导入", () =>
{
    var result = new XmindConverter().Import(fixture); var d = result.Document; var sheet = d.Sheets[0];
    Check(d.Sheets.Count == 2 && sheet.Root.Text == "光纤" && sheet.FloatingTopics.Count == 1);
    Check(sheet.Find("features")!.Collapsed && sheet.Root.Notes == "中文备注\n第二行");
    Check(sheet.Find("structure")!.Style.FontSize == 20 && sheet.Find("structure")!.Style.Fill == "#FF4343");
    Check(sheet.Find("structure")!.Image != null && sheet.Find("testing")!.Attachments.Count == 1);
    Check(sheet.Relationships.Count == 1 && sheet.Boundaries[0].Start == 0 && sheet.Summaries[0].Text == "结构与特性");
});
Test("Xmind 回写保留未知字段、样式属性、控制点和资源", () =>
{
    var converter = new XmindConverter(); var d = converter.Import(fixture).Document; d.Sheets[0].Root.Text = "修改后的中文";
    var output = Path.Combine(root, "roundtrip.xmind"); converter.Export(d, output); var entries = SafeArchive.Read(output);
    var json = JsonNode.Parse(entries["content.json"])!;
    Check(json[0]!["custom-sheet"]!["preserved"]!.GetValue<bool>());
    Check(json[0]!["rootTopic"]!["custom-topic"]!.ToString() == "保留未知字段");
    Check(json[0]!["rootTopic"]!["children"]!["attached"]![0]!["style"]!["properties"]!["custom-property"]!.ToString() == "preserved");
    Check(json[0]!["relationships"]![0]!["controlPoints"] != null && entries["custom.bin"].SequenceEqual(new byte[] { 7, 8, 9 }));
    var again = converter.Import(output).Document;
    Check(again.Sheets[0].Root.Text == "修改后的中文" && again.Sheets[0].Find("testing")!.Attachments.Count == 1 && again.Sheets[0].Find("structure")!.Image != null);
});
Test("teaMind 保存后仍能回写 Xmind 元数据", () =>
{
    var d = new XmindConverter().Import(fixture).Document; var file = Path.Combine(root, "preserved.teamind"); var store = new NativeDocumentStore(); store.Export(d, file);
    var imported = store.Import(file).Document; var output = Path.Combine(root, "preserved.xmind"); new XmindConverter().Export(imported, output);
    Check(SafeArchive.Read(output).ContainsKey("custom.bin"));
});
Test("拒绝旧版 XML Xmind", () =>
{
    var file = Path.Combine(root, "legacy.xmind"); SafeArchive.Write(file, new Dictionary<string, byte[]> { ["content.xml"] = Encoding.UTF8.GetBytes("<xmap/>" ) });
    Throws<InvalidDataException>(() => new XmindConverter().Import(file));
});
Test("Markdown 导出中文、备注与链接", () =>
{
    var d = new XmindConverter().Import(fixture).Document; var md = MarkdownExporter.Export(d);
    Check(md.Contains("光纤") && md.Contains("中文备注") && md.Contains("https://example.com") && md.Contains("第二画布"));
});
AsyncTest("AI 正常响应只生成预览，不修改原文档", async () =>
{
    var s = Session(); var before = ModelJson.Serialize(s.Document); var handler = new MockHandler(HttpStatusCode.OK, "{\"text\":\"学习计划\",\"children\":[{\"text\":\"第一步\",\"children\":[]}]}");
    var service = new CompatibleAiService(new HttpClient(handler)); var proposal = await service.ProposeAsync(new() { BaseUrl = "http://localhost:11434/v1", Model = "local" }, "secret", AiOperation.Generate, "规划学习", default);
    Check(proposal.Topic.Text == "学习计划" && proposal.Topic.Children.Count == 1 && ModelJson.Serialize(s.Document) == before);
    Check(handler.Url == "http://localhost:11434/v1/chat/completions" && handler.Authorization == "Bearer secret");
    var userMessage = JsonNode.Parse(handler.RequestBody)!["messages"]![1]!["content"]!.GetValue<string>();
    Check(userMessage == "规划学习" && !userMessage.Contains(s.Sheet.Root.Text));
});
AsyncTest("AI 鉴权失败与非法返回可报告", async () =>
{
    var settings = new AiSettings { BaseUrl = "https://example.com/v1", Model = "test" };
    await ThrowsAsync<HttpRequestException>(() => new CompatibleAiService(new HttpClient(new MockHandler(HttpStatusCode.Unauthorized, ""))).ProposeAsync(settings, "", AiOperation.Generate, "测试", default));
    await ThrowsAsync<InvalidDataException>(() => new CompatibleAiService(new HttpClient(new MockHandler(HttpStatusCode.OK, "not json"))).ProposeAsync(settings, "", AiOperation.Generate, "测试", default));
});
AsyncTest("AI 取消不产生结果，空配置不发请求", async () =>
{
    var handler = new MockHandler(HttpStatusCode.OK, "{\"text\":\"测试\"}"); var service = new CompatibleAiService(new HttpClient(handler));
    await ThrowsAsync<InvalidOperationException>(() => service.ProposeAsync(new(), "", AiOperation.Generate, "测试", default));
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => service.ProposeAsync(new() { Model = "test" }, "", AiOperation.Generate, "测试", cancellation.Token));
});
AsyncTest("AI 确认整体应用和一次撤销", async () =>
{
    var s = Session(); var before = ModelJson.Serialize(s.Document);
    var proposal = await new CompatibleAiService(new HttpClient(new MockHandler(HttpStatusCode.OK, "{\"text\":\"新分支\",\"children\":[{\"text\":\"子主题\"}]}"))).ProposeAsync(new() { Model = "test" }, "", AiOperation.Expand, "扩展", default);
    s.Execute("应用 AI", _ => s.Sheet.Root.Children.Add(proposal.Topic)); Check(s.IsDirty); s.Undo(); Check(ModelJson.Serialize(s.Document) == before && !s.IsDirty);
});
Test("1000 / 5000 节点布局与选择性能基准", () =>
{
    var measurements = new List<object>();
    foreach (var size in new[] { 1000, 5000 })
    {
        var d = new MapDocument(); var rootTopic = d.Sheets[0].Root;
        for (var i = 0; i < size / 10; i++) { var branch = new Topic { Text = "分支 " + i }; rootTopic.Children.Add(branch); for (var j = 0; j < 9; j++) branch.Children.Add(new() { Text = "中文节点 " + j }); }
        var s = new DocumentSession(d); var watch = Stopwatch.StartNew(); var scene = new MapLayoutEngine().Layout(s.Sheet); watch.Stop(); var layoutMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart(); for (var i = 0; i < 10000; i++) { s.Select(rootTopic.Id); _ = s.IsDirty; } watch.Stop();
        Check(scene.Nodes.Count == size + 1); Check(layoutMs < 5000, "布局耗时异常"); Check(watch.ElapsedMilliseconds < 1000, "选择不应重新序列化文档");
        measurements.Add(new { nodes = scene.Nodes.Count, layoutMs, tenThousandSelectionsMs = watch.Elapsed.TotalMilliseconds });
    }
    File.WriteAllText(Path.Combine(root, "performance.json"), ModelJson.Serialize(measurements));
});

Test("保存期间发生新编辑时保留未保存状态", () =>
{
    var s = Session(); s.Add(false); var snapshot = ModelJson.Clone(s.Document); s.Add(false);
    s.MarkSavedSnapshot(snapshot); Check(s.IsDirty); s.Undo(); Check(!s.IsDirty);
});
Test("无法识别的 Xmind 范围原样保留并报告", () =>
{
    var entries = SafeArchive.Read(fixture); var json = JsonNode.Parse(entries["content.json"])!;
    json[0]!["rootTopic"]!["boundaries"]!.AsArray().Add(new JsonObject { ["id"] = "unknown-boundary", ["range"] = "custom-range", ["title"] = "未知范围" });
    entries["content.json"] = Encoding.UTF8.GetBytes(json.ToJsonString()); var input = Path.Combine(root, "unsupported.xmind"); SafeArchive.Write(input, entries);
    var converter = new XmindConverter(); var result = converter.Import(input); Check(result.Warnings.Any(w => w.Contains("范围")));
    var output = Path.Combine(root, "unsupported-roundtrip.xmind"); converter.Export(result.Document, output);
    var written = JsonNode.Parse(SafeArchive.Read(output)["content.json"])!;
    Check(written[0]!["rootTopic"]!["boundaries"]!.AsArray().Any(g => g?["id"]?.ToString() == "unknown-boundary"));
});
Test("范围失效时报告且撤销恢复", () =>
{
    var d = Sample(); var sheet = d.Sheets[0]; sheet.Summaries.Add(new() { ParentId = sheet.Root.Id, Start = 0, End = 0, Text = "概要测试" });
    var s = new DocumentSession(d); s.Select(sheet.Root.Children[0].Id); s.DeleteSelected();
    Check(s.Sheet.Summaries.Count == 0 && s.Document.CompatibilityWarnings.Count == 1); s.Undo(); Check(s.Sheet.Summaries.Count == 1 && s.Document.CompatibilityWarnings.Count == 0);
});

Test("编辑预览动态扩展主题，不修改文档且可取消", () =>
{
    var sheet = new MapSheet(); var topic = new Topic { Text = "新主题" }; sheet.Root.Children.Add(topic);
    var before = ModelJson.Serialize(sheet); var engine = new MapLayoutEngine();
    var original = engine.Layout(sheet).Nodes.Single(n => n.Topic.Id == topic.Id);
    var preview = engine.Layout(sheet, textOverrides: new Dictionary<string, string> { [topic.Id] = "中文输入测试" }).Nodes.Single(n => n.Topic.Id == topic.Id);
    Check(preview.Bounds.Width > original.Bounds.Width && ModelJson.Serialize(sheet) == before);
    var longPreview = engine.Layout(sheet, textOverrides: new Dictionary<string, string> { [topic.Id] = new string('茶', 80) }).Nodes.Single(n => n.Topic.Id == topic.Id);
    Check(longPreview.Bounds.Height > original.Bounds.Height && longPreview.Lines.Count > 1);
    Check(engine.Layout(sheet).Nodes.Single(n => n.Topic.Id == topic.Id).Bounds == original.Bounds);
});

Test("连续新建主分支时左右均衡且已有分支不跳动", () =>
{
    var s = new DocumentSession(new MapDocument());
    for (var i = 0; i < 20; i++)
    {
        var oldSides = s.Sheet.Root.Children.ToDictionary(t => t.Id, t => t.Side);
        s.Select(s.Sheet.Root.Id); s.Add(false);
        Check(s.Sheet.Root.Children.All(t => !oldSides.ContainsKey(t.Id) || oldSides[t.Id] == t.Side));
        var left = s.Sheet.Root.Children.Count(t => t.Side == TopicSide.Left);
        var right = s.Sheet.Root.Children.Count(t => t.Side == TopicSide.Right);
        Check(Math.Abs(left - right) <= 1);
    }
});
Test("新主分支考虑完整子树高度而非只统计主分支数量", () =>
{
    var s = new DocumentSession(new MapDocument()); var heavy = s.Add(false);
    for (var i = 0; i < 12; i++) { s.Select(heavy.Id); s.Add(false); }
    var initialSide = heavy.Side;
    s.Select(s.Sheet.Root.Id); var light = s.Add(false); Check(light.Side != initialSide);
    s.Select(s.Sheet.Root.Id); var next = s.Add(false); Check(next.Side == light.Side, "较轻的一侧应继续容纳新分支");
});
Test("均衡左右可撤销，显式方向在保存往返后保持", () =>
{
    var d = new MapDocument(); d.Sheets[0].Root.Children = Enumerable.Range(0, 10).Select(i => new Topic { Text = "分支" + i, Side = TopicSide.Right }).ToList();
    var s = new DocumentSession(d); s.Execute("均衡", _ => MindMapBalancer.Rebalance(s.Sheet));
    Check(s.Sheet.Root.Children.Count(t => t.Side == TopicSide.Left) == 5);
    var path = Path.Combine(root, "sides.teamind"); new NativeDocumentStore().Export(s.Document, path);
    var read = new NativeDocumentStore().Import(path).Document; Check(read.Sheets[0].Root.Children.Select(t => t.Side).SequenceEqual(s.Sheet.Root.Children.Select(t => t.Side)));
    s.Undo(); Check(s.Sheet.Root.Children.All(t => t.Side == TopicSide.Right));
});

var failures = 0; var results = new List<object>();
foreach (var (name, run) in tests)
{
    var watch = Stopwatch.StartNew();
    try { await run(); Console.WriteLine("PASS " + name); results.Add(new { name, success = true, ms = watch.Elapsed.TotalMilliseconds }); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + "\n" + ex); results.Add(new { name, success = false, error = ex.ToString() }); }
}
File.WriteAllText(Path.Combine(root, "results.json"), ModelJson.Serialize(new { total = tests.Count, failures, results }));
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed. Artifacts: {root}"); return failures == 0 ? 0 : 1;

sealed class MockHandler(HttpStatusCode status, string content) : HttpMessageHandler
{
    public string? Url { get; private set; }
    public string? Authorization { get; private set; }
    public string RequestBody { get; private set; } = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Url = request.RequestUri!.ToString(); Authorization = request.Headers.Authorization?.ToString(); RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        var json = ModelJson.Serialize(new { choices = new[] { new { message = new { content } } } });
        return new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
