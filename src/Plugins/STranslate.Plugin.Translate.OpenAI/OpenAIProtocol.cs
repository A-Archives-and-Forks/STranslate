using System.Text.Json.Nodes;

namespace STranslate.Plugin.Translate.OpenAI;

internal static class OpenAIProtocol
{
    private const string ChatCompletionsPath = "/v1/chat/completions";
    private const string ResponsesPath = "/v1/responses";

    internal static string BuildFinalUrl(string url, OpenAIApiMode apiMode)
    {
        var path = apiMode == OpenAIApiMode.Responses ? ResponsesPath : ChatCompletionsPath;
        return UrlHelper.BuildFinalUrl(url, path);
    }

    internal static object CreateRequest(
        OpenAIApiMode apiMode,
        string model,
        IReadOnlyCollection<PromptItem> messages,
        double temperature)
    {
        return apiMode switch
        {
            OpenAIApiMode.Responses => new ResponsesRequest(model, messages, temperature, true, false),
            _ => new ChatCompletionsRequest(model, messages, temperature, true)
        };
    }

    internal static OpenAIStreamEvent ParseStreamLine(OpenAIApiMode apiMode, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return default;

        var payload = line.StartsWith("data:", StringComparison.Ordinal)
            ? line["data:".Length..].Trim()
            : line.Trim();

        if (payload.Length == 0 || payload.Equals("[DONE]", StringComparison.Ordinal))
            return default;

        if (!payload.StartsWith('{'))
            return default;

        JsonNode? parsedData;
        try
        {
            parsedData = JsonNode.Parse(payload);
        }
        catch
        {
            // 部分 OpenAI-compatible 服务会在 SSE 中混入非 JSON 状态行。
            return default;
        }

        if (parsedData is null)
            return default;

        var errorMessage = GetErrorMessage(parsedData);
        if (!string.IsNullOrWhiteSpace(errorMessage))
            return new OpenAIStreamEvent(null, errorMessage);

        var textDelta = apiMode switch
        {
            OpenAIApiMode.Responses when parsedData["type"]?.ToString() == "response.output_text.delta"
                => parsedData["delta"]?.ToString(),
            OpenAIApiMode.ChatCompletions
                => parsedData["choices"]?[0]?["delta"]?["content"]?.ToString(),
            _ => null
        };

        return string.IsNullOrEmpty(textDelta)
            ? default
            : new OpenAIStreamEvent(textDelta, null);
    }

    private static string? GetErrorMessage(JsonNode parsedData)
    {
        if (parsedData["type"]?.ToString() == "error")
            return parsedData["message"]?.ToString();

        if (parsedData["type"]?.ToString() == "response.failed")
            return parsedData["response"]?["error"]?["message"]?.ToString();

        return parsedData["error"]?["message"]?.ToString();
    }
}

internal sealed record ChatCompletionsRequest(
    string Model,
    IReadOnlyCollection<PromptItem> Messages,
    double Temperature,
    bool Stream);

internal sealed record ResponsesRequest(
    string Model,
    IReadOnlyCollection<PromptItem> Input,
    double Temperature,
    bool Stream,
    bool Store);

internal readonly record struct OpenAIStreamEvent(string? TextDelta, string? ErrorMessage);
