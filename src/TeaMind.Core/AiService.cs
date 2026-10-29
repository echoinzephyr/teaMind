using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeaMind.Core;

public sealed class AiSettings
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    public string ProtectedKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 90;
}
public enum AiOperation { Generate, Expand, Summarize }
public sealed record AiProposal(AiOperation Operation, Topic Topic, string Preview);
public interface IAiService
{
    Task<AiProposal> ProposeAsync(AiSettings settings, string key, AiOperation operation, string input, CancellationToken cancellationToken);
}
public sealed class CompatibleAiService(HttpClient client) : IAiService
{
    public async Task<AiProposal> ProposeAsync(AiSettings settings, string key, AiOperation operation, string input, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(settings.BaseUrl.TrimEnd('/') + "/chat/completions", UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("请填写有效的 HTTP 或 HTTPS 服务地址。");
        if (string.IsNullOrWhiteSpace(settings.Model)) throw new InvalidOperationException("请先设置模型名称。");
        if (string.IsNullOrWhiteSpace(input)) throw new InvalidOperationException("请输入内容或选择分支。");
        if (input.Length > 60000) throw new InvalidOperationException("输入超过 60,000 字符，请缩小选择范围。");
        var instruction = operation switch { AiOperation.Expand => "扩展选中的主题，返回有用的子主题。", AiOperation.Summarize => "将提供的内容整理为简洁且有层次的总结导图。", _ => "将用户的内容整理成思维导图。" };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new { model = settings.Model, temperature = 0.6, messages = new[] {
            new { role = "system", content = instruction + "仅返回 JSON 对象，不要代码围栏。格式：{\"text\":\"中心主题\",\"children\":[{\"text\":\"分支\",\"children\":[]}]}。不超过 200 个主题和 8 层。使用用户的语言。" },
            new { role = "user", content = input }
        } });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 10, 300)));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(response.StatusCode switch { System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "AI 服务鉴权失败，请检查密钥和权限。", System.Net.HttpStatusCode.TooManyRequests => "AI 服务请求过多或额度不足，请稍后重试。", _ => $"AI 服务返回错误 {(int)response.StatusCode}。" });
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var memory = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (memory.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("AI 返回内容过大。");
                memory.Write(buffer, 0, read);
            }
            var body = JsonNode.Parse(memory.ToArray()) ?? throw new InvalidDataException("AI 返回为空。");
            var content = body["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? throw new InvalidDataException("AI 返回缺少内容。");
            content = content.Trim();
            if (content.StartsWith("```")) { var newline = content.IndexOf('\n'); content = content[(newline + 1)..]; var end = content.LastIndexOf("```", StringComparison.Ordinal); if (end >= 0) content = content[..end]; }
            var count = 0;
            var topic = ParseTopic(JsonNode.Parse(content, documentOptions: new() { MaxDepth = 32 }) ?? throw new InvalidDataException("AI 返回为空。"), 0, ref count);
            return new(operation, topic, MarkdownExporter.TopicToMarkdown(topic));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("AI 请求超时，请检查服务后重试。"); }
        catch (JsonException e) { throw new InvalidDataException("AI 未返回有效的导图 JSON，请重试。", e); }
    }
    private static Topic ParseTopic(JsonNode node, int depth, ref int count)
    {
        if (depth > 8 || ++count > 200) throw new InvalidDataException("AI 返回超过 200 个主题或 8 层。");
        var text = node["text"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4000) throw new InvalidDataException("AI 返回的主题文字为空或过长。");
        var topic = new Topic { Text = text };
        if (node["children"] is JsonArray children)
            foreach (var child in children) topic.Children.Add(ParseTopic(child ?? throw new InvalidDataException("AI 子主题无效。"), depth + 1, ref count));
        return topic;
    }
}
