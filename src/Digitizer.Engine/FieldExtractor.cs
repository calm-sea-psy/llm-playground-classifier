using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;

namespace Digitizer.Engine;

/// <param name="MaxOutputTokens">같은 내용을 끝없이 생성해 시간 초과로 작업 전체가 실패하는 것을 막는 상한 (평가 도구 LlmOptions 와 같은 4096)</param>
public sealed record LlmOptions(string Model, int ContextLength = 16384, string KeepAlive = "10m", bool CpuOnly = false, int? MaxOutputTokens = null);

/// <param name="Truncated">최대 출력 토큰에서 잘림 (done_reason=length)</param>
public sealed record Extraction(JsonObject? Fields, string Raw, long ElapsedMs, long? InputTokens, long? OutputTokens, int Attempts, string? Error, bool Truncated = false);

/// <summary>이미지 입력 (VLM 폴백)</summary>
public sealed record ImageInput(byte[] Data, string MediaType);

/// <summary>
/// 원문 ➔ 필드 (LLM 1회, 형식 오류면 1회 재시도). 스키마는 문서 종류 정의에서 만든다.
/// 도구 없이 JSON 스키마(format)만 씀, think 끔, num_ctx 지정 (docs/agent_evaluation.md 에서 확인한 설정)
/// </summary>
public sealed class FieldExtractor(HttpClient ollamaHttp, LlmOptions options)
{
    /// <summary>원문 종류별 안내 문구 (영수증 user.md 의 {{$input_label}}). ocr 은 평가 도구 DocumentText.Label 과 같은 문장</summary>
    public static string InputLabel(string? source) => source switch
    {
        "ocr" => "OCR 텍스트 (위➔아래, 같은 줄은 왼쪽➔오른쪽 순서)",
        "pdf-text" or "docx" => "문서 텍스트 (PDF · DOCX 에서 추출, 원래 순서)",
        _ => "문서 텍스트",
    };

    public Task<Extraction> ExtractAsync(DocumentType type, string sourceText, string? sourceKind = null, CancellationToken ct = default) =>
        ExtractMessagesAsync(type, type.SystemPromptFor(options.Model), type.UserMessage(InputLabel(sourceKind), sourceText), null, ct);

    /// <summary>
    /// 지시문 · 사용자 메시지를 이미 만든 경우 (평가 도구는 프롬프트 관리 화면의 적용 버전으로 렌더링해서 넘김).
    /// images 가 있으면 VLM 폴백 (이미지 + 원문)
    /// </summary>
    public async Task<Extraction> ExtractMessagesAsync(DocumentType type, string system, string user, IReadOnlyList<ImageInput>? images, CancellationToken ct = default)
    {
        IChatClient client = new OllamaApiClient(ollamaHttp, options.Model);
        var chat = new ChatOptions
        {
            Temperature = 0,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema(type), type.Id),
            AdditionalProperties = [],
        };
        // AddOllamaOption 도 AdditionalProperties 에 넣으므로 순서 주의 (덮어쓰면 num_ctx 가 빠져 4096 에서 잘림)
        chat.AdditionalProperties["think"] = false;
        chat.AdditionalProperties["keep_alive"] = options.KeepAlive;
        chat.AddOllamaOption(OllamaOption.NumCtx, options.ContextLength);
        if (options.CpuOnly) chat.AddOllamaOption(OllamaOption.NumGpu, 0);
        if (options.MaxOutputTokens is { } max) chat.MaxOutputTokens = max;

        var userMessage = new ChatMessage(ChatRole.User, user);
        foreach (var image in images ?? []) userMessage.Contents.Add(new DataContent(image.Data, image.MediaType));

        var sw = Stopwatch.StartNew();
        string raw = "";
        var truncated = false;
        long? input = 0, output = 0;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var response = await client.GetResponseAsync([new(ChatRole.System, system), userMessage], chat, ct);
            raw = response.Text;
            // qwen3-vl 은 think 를 꺼도 답을 thinking 필드에만 넣을 때가 있음 (평가 도구 OllamaChatCompletionService 와 같은 대체)
            if (string.IsNullOrWhiteSpace(raw))
                raw = string.Concat(response.Messages.SelectMany(m => m.Contents).OfType<TextReasoningContent>().Select(r => r.Text));
            truncated = response.FinishReason == ChatFinishReason.Length;
            input += response.Usage?.InputTokenCount ?? 0;
            output += response.Usage?.OutputTokenCount ?? 0;
            try
            {
                if (JsonNode.Parse(raw) is JsonObject fields)
                    return new Extraction(fields, raw, sw.ElapsedMilliseconds, input, output, attempt, null, truncated);
            }
            catch (JsonException) { }
        }
        return new Extraction(null, raw, sw.ElapsedMilliseconds, input, output, 2,
            truncated ? "응답이 최대 길이에서 잘림 (같은 내용 반복 생성 의심)" : "JSON 형식 오류 (재시도 후에도)", truncated);
    }

    /// <summary>문서 종류 정의 ➔ JSON 스키마. 모든 필드를 required 로 두고 값이 없으면 null (모델이 필드를 빼먹지 않게)</summary>
    public static JsonElement Schema(DocumentType type)
    {
        static JsonObject Field(FieldDef f) => f.Type switch
        {
            "list" => new JsonObject
            {
                ["type"] = "array",
                ["description"] = f.Description is null ? f.Label : $"{f.Label}. {f.Description}",
                ["items"] = Object(f.Items ?? []),
            },
            "string_list" => new JsonObject { ["type"] = "array", ["description"] = f.Label, ["items"] = new JsonObject { ["type"] = "string" } },
            // 칸마다 빈 값(null)이 올 수 있는 문자열 목록 (보험 청구서 다른 보험사 칸, 평가 도구 DocumentSchemas 와 같음)
            "text_list" => new JsonObject { ["type"] = "array", ["description"] = f.Label, ["items"] = new JsonObject { ["type"] = new JsonArray("string", "null") } },
            // 영수증: 금액은 원 단위 정수, 수량은 소수 가능 (평가 도구 DocumentSchemas 와 같은 타입)
            "amount" => new JsonObject { ["type"] = new JsonArray("integer", "null"), ["description"] = Describe(f) },
            "number" => new JsonObject { ["type"] = new JsonArray("number", "null"), ["description"] = Describe(f) },
            _ => new JsonObject { ["type"] = new JsonArray("string", "null"), ["description"] = Describe(f) },
        };

        static JsonObject Object(List<FieldDef> fields) => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(fields.Select(f => KeyValuePair.Create(f.Name, (JsonNode?)Field(f)))),
            ["required"] = new JsonArray([.. fields.Select(f => (JsonNode)JsonValue.Create(f.Name)!)]),
            ["additionalProperties"] = false,
        };

        // 팩의 description 을 그대로 싣는다 (v0.1: 시험 이름 · 점수, 대학원 포함 여부가 모호해 모델마다 다르게 옮김)
        static string Describe(FieldDef f) => (f.Type switch
        {
            "date" => $"{f.Label} (YYYY-MM-DD)",
            "month" => $"{f.Label} (YYYY-MM)",
            "month_or_present" => $"{f.Label} (YYYY-MM, 재직 중 · 현재면 present)",
            "time" => $"{f.Label} (HH:MM 또는 HH:MM:SS)",
            _ => f.Label,
        }) + (f.Description is null ? "" : $". {f.Description}");

        return JsonDocument.Parse(Object(type.Fields).ToJsonString()).RootElement.Clone();
    }
}
