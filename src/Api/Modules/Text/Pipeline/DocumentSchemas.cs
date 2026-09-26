using Digitizer.Engine.Rules;
using System.Text.Json.Nodes;

namespace Api.Modules.Text.Pipeline;

/// <summary>
/// 문서 종류 분류에 강제할 JSON 스키마 (Ollama format).
/// 필드 추출 스키마는 문서 종류 팩(packs/{종류}/type.json)에서 Engine 이 만든다 (4차 통합에서 옮김, 기존 스키마와 같음을
/// tests/Api.Tests/Fixtures/schema.*.json 으로 고정)
/// </summary>
public static class DocumentSchemas
{
    public static JsonObject Classification() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["document_type"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray([.. DocumentTypes.All.Select(t => JsonValue.Create(t))]),
            },
        },
        ["required"] = new JsonArray("document_type"),
        ["additionalProperties"] = false,
    };
}
