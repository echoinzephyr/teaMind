using System.Text;

namespace TeaMind.Core;

public static class MarkdownExporter
{
    public static string Export(MapDocument document)
    {
        var sb = new StringBuilder();
        foreach (var sheet in document.Sheets)
        {
            sb.AppendLine("# " + Escape(sheet.Title)).AppendLine();
            sb.Append(TopicToMarkdown(sheet.Root));
            foreach (var floating in sheet.FloatingTopics) sb.Append(TopicToMarkdown(floating));
            sb.AppendLine();
        }
        return sb.ToString();
    }
    public static string TopicToMarkdown(Topic topic)
    {
        var sb = new StringBuilder(); Walk(topic, 0); return sb.ToString();
        void Walk(Topic t, int depth)
        {
            var indent = new string(' ', depth * 2);
            sb.Append(indent).Append("- ").AppendLine(Escape(t.Text));
            if (t.Notes.Length > 0) sb.Append(indent).Append("  > ").AppendLine(t.Notes.Replace("\r", "").Replace("\n", "\n" + indent + "  > "));
            if (t.Link.Length > 0) sb.Append(indent).Append("  链接：").AppendLine(t.Link);
            if (t.Tags.Count > 0) sb.Append(indent).Append("  标签：").AppendLine(string.Join("、", t.Tags));
            foreach (var child in t.Children) Walk(child, depth + 1);
        }
    }
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("*", "\\*").Replace("[", "\\[").Replace("]", "\\]").Replace("\r", "").Replace("\n", " / ");
}
