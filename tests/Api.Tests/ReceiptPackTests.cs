using System.Text.Json.Nodes;
using Api.Modules.Text.Pipeline;
using Api.Shared.Prompts;
using Digitizer.Engine;
using Digitizer.Engine.Rules;

namespace Api.Tests;

/// <summary>
/// 4차 통합 B: 영수증 팩(packs/receipt)이 기존 Text 모듈 영수증 처리와 같게 동작하는지.
/// C 단계(Text 모듈이 Engine 으로 추출)에서 KORIE 150장 재측정 전에 구조 차이부터 없앰
/// </summary>
public class ReceiptPackTests
{
    private static readonly string Repo = FindRepo();
    private static readonly DocumentType Pack = DocumentType.Load(Path.Combine(Repo, "packs", "receipt"));
    private static readonly string PromptDir = Path.Combine(Repo, "src", "Api", "Modules", "Text", "Pipeline", "Prompts");
    /// <summary>API 가 실행 때 읽는 프롬프트 폴더 (빌드 결과물). 영수증 지시문은 여기에 팩 파일이 기존 이름으로 복사됨</summary>
    private static readonly string OutputPromptDir = PromptStore.PromptsDirectory("text");

    [Fact]
    public void 스키마가_기존과_같다()
    {
        var engine = JsonNode.Parse(FieldExtractor.Schema(Pack).GetRawText())!;
        var legacy = DocumentSchemas.For(DocumentTypes.Receipt, amountsAsString: false);
        // 설명(description)은 Ollama 문법 제약에 영향이 없어 빼고 비교
        Assert.True(JsonNode.DeepEquals(StripDescriptions(engine), StripDescriptions(legacy)),
            $"팩 스키마\n{StripDescriptions(engine).ToJsonString()}\n기존 스키마\n{StripDescriptions(legacy).ToJsonString()}");
    }

    /// <summary>영수증 지시문은 팩이 원본 하나: 소스 폴더에는 없고, 빌드 때 팩 파일이 기존 이름으로 출력 폴더에 들어감</summary>
    [Theory]
    [InlineData("prompt.md", "extract.receipt.md")]
    [InlineData("prompt.qwen3-vl.md", "extract.receipt.qwen3-vl.md")]
    public void 영수증_지시문은_팩이_원본이다(string packFile, string apiFile)
    {
        Assert.False(File.Exists(Path.Combine(PromptDir, apiFile)), $"{apiFile} 이 소스 폴더에 다시 생겼습니다 (원본은 packs/receipt/{packFile})");
        Assert.Equal(Read(Path.Combine(Repo, "packs", "receipt", packFile)), Read(Path.Combine(OutputPromptDir, apiFile)));
    }

    /// <summary>원문 전달 틀은 상업송장 · 보험 청구서도 같이 쓰는 공용 파일이라 API 에 남기고 팩은 복사본 ➔ 같은지 확인</summary>
    [Theory]
    [InlineData("user.md", "extract.user.md")]
    [InlineData("vlm.user.md", "extract.vlm.user.md")]
    public void 원문_전달_틀은_공용_파일과_같다(string packFile, string apiFile) =>
        Assert.Equal(Read(Path.Combine(PromptDir, apiFile)), Read(Path.Combine(Repo, "packs", "receipt", packFile)));

    [Theory]
    [InlineData("gemma4:12b", "extract.receipt.md")]
    [InlineData("qwen3-vl:8b-instruct", "extract.receipt.qwen3-vl.md")]
    public void 지시문은_모델_계열별_파일에_금액_규칙을_넣는다(string model, string apiFile)
    {
        var expected = Read(Path.Combine(OutputPromptDir, apiFile))
            .Replace("{{$amount_rule}}", TextPipeline.AmountRule(DocumentTypes.Receipt, asString: false));
        Assert.Equal(expected, Pack.SystemPromptFor(model).Replace("\r\n", "\n"));
    }

    [Fact]
    public void 사용자_메시지는_기존_OCR_안내_문구와_같다()
    {
        var label = new DocumentText("", Structured: false).Label;
        Assert.Equal(label, FieldExtractor.InputLabel("ocr"));
        Assert.Equal($"{label}:\n줄1\n줄2", Pack.UserMessage(label, "줄1\n줄2").Replace("\r\n", "\n").TrimEnd());
    }

    // 수량 보정(금액 = 단가인데 수량 7) · 품목 합 초과 · 사업자번호 체크섬 · 근거 없는 날짜가 모두 걸리는 예
    private const string Fields = """
        {
          "store_name": "가게", "business_no": "124-81-00999", "date": "2024-01-01", "time": "13:05",
          "receipt_no": null, "subtotal": null, "tax": null, "gross_total": null, "total": 9000, "payment_method": "카드",
          "items": [ { "name": "새우깡", "qty": 7, "unit_price": 1000, "amount": 1000 },
                     { "name": "라면", "qty": 2, "unit_price": 5000, "amount": 10000 } ]
        }
        """;
    private const string Ocr = "가게\n124-81-00999\n2023-02-26 13:05\n새우깡 1,000 7 1,000\n라면 5,000 2 10,000\n합계 9,000";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 검증은_기존_보정_규칙_근거확인_순서와_같다(bool fromImage)
    {
        var legacyFields = JsonNode.Parse(Fields)!.AsObject();
        List<ValidationIssue> legacy =
        [
            .. FieldValidator.CorrectQuantities(DocumentTypes.Receipt, legacyFields),
            .. FieldValidator.Validate(DocumentTypes.Receipt, legacyFields),
            .. fromImage ? [] : FieldValidator.CheckGrounded(DocumentTypes.Receipt, legacyFields, Ocr),
        ];
        var engineFields = JsonNode.Parse(Fields)!.AsObject();
        var engine = Validator.Validate(Pack, engineFields, Ocr, fromImage);

        Assert.Equal(legacy, engine);
        Assert.Equal(legacyFields.ToJsonString(), engineFields.ToJsonString());  // 수량 보정 결과도 같음
        Assert.Contains(legacy, i => i.Rule == "item_qty_corrected");
        Assert.Equal(!fromImage, legacy.Any(i => i.Rule == "grounded"));
    }

    [Fact]
    public void 이력서_팩도_등록된_규칙만_쓴다()
    {
        var resume = DocumentType.Load(Path.Combine(Repo, "packs", "resume"));
        Assert.All(resume.Rules, r => Assert.True(RuleRegistry.IsKnown(r)));
        Assert.True(resume.GenericChecks);
    }

    private static JsonNode StripDescriptions(JsonNode node)
    {
        var copy = node.DeepClone();
        void Walk(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                o.Remove("description");
                foreach (var (_, v) in o.ToList()) Walk(v);
            }
            else if (n is JsonArray a)
            {
                foreach (var v in a) Walk(v);
            }
        }
        Walk(copy);
        return copy;
    }

    private static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    private static string FindRepo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "llm-playground-classifier.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("저장소 루트를 찾지 못했습니다");
    }
}
