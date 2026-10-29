namespace TeaMind.Core;

public interface IEditCommand
{
    string Description { get; }
    void Apply(MapDocument document);
}
public sealed record EditCommand(string Description, Action<MapDocument> Action) : IEditCommand
{
    public void Apply(MapDocument document) => Action(document);
}
public sealed class DocumentSession
{
    private readonly Stack<(string Json, string Description)> undo = new();
    private readonly Stack<(string Json, string Description)> redo = new();
    private string savedState;
    private string currentState;
    public MapDocument Document { get; private set; }
    public string? FilePath { get; set; }
    public string RecoveryId { get; } = Guid.NewGuid().ToString("N");
    public int SheetIndex { get; set; }
    public MapSheet Sheet => Document.Sheets[Math.Clamp(SheetIndex, 0, Document.Sheets.Count - 1)];
    public HashSet<string> SelectedIds { get; } = [];
    public Topic Selected => Sheet.AllTopics().FirstOrDefault(x => SelectedIds.Contains(x.Id)) ?? Sheet.Root;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool IsDirty => currentState != savedState;
    public long Revision { get; private set; }
    public event Action? Changed;
    public DocumentSession(MapDocument document, bool dirty = false)
    {
        DocumentValidator.Validate(document);
        foreach (var sheet in document.Sheets) MindMapBalancer.Assign(sheet);
        Document = document;
        currentState = ModelJson.Serialize(document);
        savedState = dirty ? "" : currentState;
        SelectedIds.Add(Sheet.Root.Id);
    }
    public void Execute(string description, Action<MapDocument> action) => Execute(new EditCommand(description, action));
    public void Execute(IEditCommand command)
    {
        var before = currentState;
        try { command.Apply(Document); DocumentValidator.Validate(Document); }
        catch { Document = ModelJson.Deserialize<MapDocument>(before); throw; }
        var after = ModelJson.Serialize(Document);
        if (before == after) return;
        currentState = after; Revision++;
        undo.Push((before, command.Description));
        redo.Clear();
        Changed?.Invoke();
    }
    public void Undo() => Restore(undo, redo);
    public void Redo() => Restore(redo, undo);
    private void Restore(Stack<(string Json, string Description)> from, Stack<(string Json, string Description)> to)
    {
        if (!from.TryPop(out var state)) return;
        to.Push((currentState, state.Description));
        Document = ModelJson.Deserialize<MapDocument>(state.Json);
        currentState = state.Json; Revision++;
        SheetIndex = Math.Clamp(SheetIndex, 0, Document.Sheets.Count - 1);
        SelectedIds.RemoveWhere(id => Sheet.Find(id) == null);
        if (SelectedIds.Count == 0) SelectedIds.Add(Sheet.Root.Id);
        Changed?.Invoke();
    }
    public void MarkSaved() { currentState = savedState = ModelJson.Serialize(Document); Changed?.Invoke(); }
    public void MarkSavedSnapshot(MapDocument snapshot) { savedState = ModelJson.Serialize(snapshot); Changed?.Invoke(); }
    public Topic Add(bool sibling)
    {
        var added = new Topic();
        var selectedId = Selected.Id;
        Execute("添加主题", _ =>
        {
            var groups = CaptureGroups(Sheet);
            var selected = Sheet.Find(selectedId)!;
            var parent = sibling ? Sheet.ParentOf(selectedId) : null;
            if (parent != null) parent.Children.Insert(parent.Children.IndexOf(selected) + 1, added);
            else { selected.Children.Add(added); selected.Collapsed = false; }
            MindMapBalancer.Assign(Sheet);
            ReconcileGroups(Sheet, groups);
        });
        Select(added.Id);
        return added;
    }
    public void Select(string id, bool toggle = false)
    {
        if (!toggle) SelectedIds.Clear();
        if (!SelectedIds.Add(id) && toggle) SelectedIds.Remove(id);
    }
    public void DeleteSelected()
    {
        var ids = SelectedIds.ToHashSet();
        if (ids.Contains(Sheet.Root.Id)) ids.Remove(Sheet.Root.Id);
        if (ids.Count == 0) return;
        Execute("删除主题", _ =>
        {
            var groups = CaptureGroups(Sheet);
            var removed = Sheet.AllTopics().Where(t => ids.Contains(t.Id)).SelectMany(t => t.DescendantsAndSelf()).Select(t => t.Id).ToHashSet();
            foreach (var parent in Sheet.AllTopics().ToList()) parent.Children.RemoveAll(t => ids.Contains(t.Id));
            Sheet.FloatingTopics.RemoveAll(t => ids.Contains(t.Id));
            Sheet.Relationships.RemoveAll(r => removed.Contains(r.From) || removed.Contains(r.To));
            Sheet.Boundaries.RemoveAll(g => removed.Contains(g.ParentId));
            Sheet.Summaries.RemoveAll(g => removed.Contains(g.ParentId));
            ReconcileGroups(Sheet, groups);
        });
        Select(Sheet.Root.Id);
    }
    public void Move(string id, string parentId, int? index = null)
    {
        var topic = Sheet.Find(id) ?? throw new InvalidOperationException("主题不存在。");
        var parent = Sheet.Find(parentId) ?? throw new InvalidOperationException("目标主题不存在。");
        if (id == Sheet.Root.Id || topic.DescendantsAndSelf().Any(t => t.Id == parentId))
            throw new InvalidOperationException("不能将主题移入自身或其后代。");
        Execute("移动主题", _ =>
        {
            var groups = CaptureGroups(Sheet);
            var oldParent = Sheet.ParentOf(id);
            var oldIndex = oldParent?.Children.IndexOf(topic) ?? -1;
            var insertIndex = index ?? parent.Children.Count;
            if (oldParent == parent && oldIndex >= 0 && oldIndex < insertIndex) insertIndex--;
            Sheet.ParentOf(id)?.Children.Remove(topic);
            Sheet.FloatingTopics.Remove(topic);
            if (parent == Sheet.Root && oldParent != parent) topic.Side = TopicSide.Auto;
            parent.Children.Insert(Math.Clamp(insertIndex, 0, parent.Children.Count), topic);
            parent.Collapsed = false;
            MindMapBalancer.Assign(Sheet);
            ReconcileGroups(Sheet, groups);
        });
    }
    public void Paste(IEnumerable<Topic> topics)
    {
        var copies = topics.Select(CloneWithNewIds).ToList();
        Execute("粘贴主题", _ => { Selected.Children.AddRange(copies); Selected.Collapsed = false; MindMapBalancer.Assign(Sheet); });
        SelectedIds.Clear();
        foreach (var topic in copies) SelectedIds.Add(topic.Id);
    }
    public static Topic CloneWithNewIds(Topic topic)
    {
        var copy = ModelJson.Clone(topic);
        foreach (var child in copy.DescendantsAndSelf()) { child.Id = Guid.NewGuid().ToString("N"); child.XmindData = null; child.Side = TopicSide.Auto; }
        return copy;
    }
    private static Dictionary<string, List<string>> CaptureGroups(MapSheet sheet)
    {
        return sheet.Boundaries.Concat(sheet.Summaries).ToDictionary(g => g.Id, g => sheet.Find(g.ParentId)?.Children.Skip(g.Start).Take(g.End - g.Start + 1).Select(t => t.Id).ToList() ?? []);
    }
    private void ReconcileGroups(MapSheet sheet, Dictionary<string, List<string>> previous)
    {
        foreach (var list in new[] { sheet.Boundaries, sheet.Summaries })
            foreach (var group in list.ToList())
            {
                var parent = sheet.Find(group.ParentId);
                var ids = previous.GetValueOrDefault(group.Id) ?? [];
                var indices = parent?.Children.Select((t, i) => (t, i)).Where(pair => ids.Contains(pair.t.Id)).Select(pair => pair.i).ToList() ?? [];
                if (indices.Count == 0)
                {
                    list.Remove(group);
                    var message = $"“{group.Text}”的范围因结构编辑失效，已移除；可撤销恢复。";
                    if (!Document.CompatibilityWarnings.Contains(message)) Document.CompatibilityWarnings.Add(message);
                }
                else { group.Start = indices.Min(); group.End = indices.Max(); }
            }
    }
}
public static class DocumentValidator
{
    public static void Validate(MapDocument document)
    {
        if (document.Version != 1) throw new InvalidDataException($"不支持文档版本 {document.Version}。");
        if (document.Sheets.Count is 0 or > 100) throw new InvalidDataException("文档必须包含 1 到 100 个画布。");
        var ids = new HashSet<string>();
        var count = 0;
        foreach (var sheet in document.Sheets)
        {
            if (!ids.Add(sheet.Id)) throw new InvalidDataException("画布标识重复。");
            foreach (var root in new[] { sheet.Root }.Concat(sheet.FloatingTopics)) Walk(root, 0);
        }
        void Walk(Topic topic, int depth)
        {
            if (depth > 100 || ++count > 50000) throw new InvalidDataException("导图过大或层级超过 100 层。");
            if (string.IsNullOrWhiteSpace(topic.Id) || !ids.Add(topic.Id)) throw new InvalidDataException("主题标识缺失或重复。");
            if (topic.Text.Length > 100000) throw new InvalidDataException("单个主题文字过长。");
            if (topic.Style.FontSize is { } size && (!double.IsFinite(size) || size is < 8 or > 96)) throw new InvalidDataException("字号必须为 8 到 96。");
            foreach (var child in topic.Children) Walk(child, depth + 1);
        }
    }
}
