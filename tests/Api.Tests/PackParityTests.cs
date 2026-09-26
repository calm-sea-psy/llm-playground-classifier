using System.Text.Json.Nodes;
using Api.Modules.Text.Pipeline;
using Api.Shared.Prompts;
using Digitizer.Engine;
using Digitizer.Engine.Rules;

namespace Api.Tests;

/// <summary>
/// 문서 종류 팩(packs/receipt · commercial_invoice · insurance_claim)이 기존 Text 모듈 추출과 같게 동작하는지.
/// 4차 통합: KORIE 150장 · AI Hub 30장씩에서 결과가 같음을 확인하고 기존(legacy) 추출을 지움.
/// 기존 스키마는 지우기 전에 Fixtures/schema.*.json 으로 떠 둠 ➔ 팩 스키마가 의도치 않게 바뀌면 실패
/// </summary>
public class PackParityTests
{
    private static readonly string Repo = FindRepo();
    private static readonly string PromptDir = Path.Combine(Repo, "src", "Api", "Modules", "Text", "Pipeline", "Prompts");
    /// <summary>API 가 실행 때 읽는 프롬프트 폴더 (빌드 결과물). 추출 지시문은 여기에 팩 파일이 기존 이름으로 복사됨</summary>
    private static readonly string OutputPromptDir = PromptStore.PromptsDirectory("text");

    private static DocumentType Pack(string type) => DocumentType.Load(Path.Combine(Repo, "packs", type));

    public static TheoryData<string> Types => [DocumentTypes.Receipt, DocumentTypes.CommercialInvoice, DocumentTypes.InsuranceClaim];

    [Theory]
    [MemberData(nameof(Types))]
    public void 스키마가_기존과_같다(string type)
    {
        var engine = JsonNode.Parse(FieldExtractor.Schema(Pack(type)).GetRawText())!;
        // 기존 DocumentSchemas.For(종류, 금액 문자열=false) 를 지우기 전에 떠 둔 스키마
        var legacy = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo, "tests", "Api.Tests", "Fixtures", $"schema.{type}.json")))!;
        // 설명(description)은 Ollama 문법 제약에 영향이 없어 빼고 비교
        Assert.True(JsonNode.DeepEquals(StripDescriptions(engine), legacy),
            $"팩 스키마\n{StripDescriptions(engine).ToJsonString()}\n기존 스키마\n{legacy.ToJsonString()}");
    }

    /// <summary>추출 지시문은 팩이 원본 하나: 소스 폴더에는 없고, 빌드 때 팩 파일이 기존 이름으로 출력 폴더에 들어감</summary>
    [Theory]
    [InlineData("receipt", "prompt.md", "extract.receipt.md")]
    [InlineData("receipt", "prompt.qwen3-vl.md", "extract.receipt.qwen3-vl.md")]
    [InlineData("commercial_invoice", "prompt.md", "extract.commercial_invoice.md")]
    [InlineData("insurance_claim", "prompt.md", "extract.insurance_claim.md")]
    public void 추출_지시문은_팩이_원본이다(string type, string packFile, string apiFile)
    {
        Assert.False(File.Exists(Path.Combine(PromptDir, apiFile)), $"{apiFile} 이 소스 폴더에 다시 생겼습니다 (원본은 packs/{type}/{packFile})");
        Assert.Equal(Read(Path.Combine(Repo, "packs", type, packFile)), Read(Path.Combine(OutputPromptDir, apiFile)));
    }

    /// <summary>원문 전달 틀은 평가 도구 공용 파일, 팩은 복사본 ➔ 같은지 확인</summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void 원문_전달_틀은_공용_파일과_같다(string type)
    {
        Assert.Equal(Read(Path.Combine(PromptDir, "extract.user.md")), Read(Path.Combine(Repo, "packs", type, "user.md")));
        Assert.Equal(Read(Path.Combine(PromptDir, "extract.vlm.user.md")), Read(Path.Combine(Repo, "packs", type, "vlm.user.md")));
    }

    private const string WonRule = "금액은 원 단위 정수로, 쉼표·'원'·통화 기호를 뺍니다 (예: \"12,850원\" → 12850).";
    private const string InvoiceRule = "Amounts are plain numbers without thousands separators or currency symbols (e.g. \"1,234.50\" → 1234.50).";

    // 금액 규칙 문장: 지운 TextPipeline.AmountRule(종류, 금액 문자열=false) 과 같은 값이 팩 variables 에 있어야 함
    [Theory]
    [InlineData("receipt", "gemma4:12b", "extract.receipt.md", WonRule)]
    [InlineData("receipt", "qwen3-vl:8b-instruct", "extract.receipt.qwen3-vl.md", WonRule)]
    [InlineData("commercial_invoice", "gemma4:12b", "extract.commercial_invoice.md", InvoiceRule)]
    [InlineData("commercial_invoice", "qwen3-vl:8b-instruct", "extract.commercial_invoice.md", InvoiceRule)]
    [InlineData("insurance_claim", "gemma4:12b", "extract.insurance_claim.md", "")]
    public void 지시문은_모델_계열별_파일에_금액_규칙을_넣는다(string type, string model, string apiFile, string amountRule)
    {
        var expected = Read(Path.Combine(OutputPromptDir, apiFile)).Replace("{{$amount_rule}}", amountRule);
        Assert.Equal(expected, Pack(type).SystemPromptFor(model).Replace("\r\n", "\n"));
    }

    [Fact]
    public void 사용자_메시지는_기존_OCR_안내_문구와_같다()
    {
        var label = new DocumentText("", Structured: false).Label;
        Assert.Equal(label, FieldExtractor.InputLabel("ocr"));
        Assert.Equal($"{label}:\n줄1\n줄2", Pack(DocumentTypes.Receipt).UserMessage(label, "줄1\n줄2").Replace("\r\n", "\n").TrimEnd());
    }

    private const string ReceiptJson = """
        { "store_name": "가게", "business_no": "124-81-00999", "date": "2024-01-01", "time": "13:05",
          "receipt_no": null, "subtotal": null, "tax": null, "gross_total": null, "total": 9000, "payment_method": "카드",
          "items": [ { "name": "새우깡", "qty": 7, "unit_price": 1000, "amount": 1000 },
                     { "name": "라면", "qty": 2, "unit_price": 5000, "amount": 10000 } ] }
        """;
    private const string ReceiptOcr = "가게\n124-81-00999\n2023-02-26 13:05\n새우깡 1,000 7 1,000\n라면 5,000 2 10,000\n합계 9,000";

    // 종류마다 규칙이 걸리는 예: 영수증(수량 보정 · 품목 합 초과 · 체크섬 · 근거 없는 날짜), 송장(통화 코드 · 산술 경고), 청구서(이름 없음 · 형식 경고)
    public static TheoryData<string, string, string> Samples => new()
    {
        { DocumentTypes.Receipt, ReceiptJson, ReceiptOcr },
        {
            DocumentTypes.CommercialInvoice,
            """
            { "invoice_no": "INV-1", "invoice_date": "2024-13-01", "seller": "A", "buyer": "B", "currency": "WON",
              "items": [ { "description": "x", "qty": 2, "unit_price": 5.64, "amount": 54.83 } ], "total_amount": 60 }
            """,
            "COMMERCIAL INVOICE INV-1"
        },
        {
            DocumentTypes.InsuranceClaim,
            """
            { "insured_name": null, "resident_no": "12345", "company_name": null, "department": null, "job_duty": null,
              "address": null, "contact_name": null, "contact_relationship": null, "contact_phone": "abc", "email": "x",
              "other_insurers": [null], "accident_datetime": null, "accident_place": null, "diagnosis": null, "hospital": null,
              "injury_part": null, "bank_name": null, "account_no": "1", "account_holder": null, "written_date": null, "claimant_name": null }
            """,
            "보험금 청구서"
        },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void 검증은_기존_보정_규칙_근거확인과_같다(string type, string json, string ocr)
    {
        foreach (var fromImage in new[] { false, true })
        {
            var legacyFields = JsonNode.Parse(json)!.AsObject();
            List<ValidationIssue> legacy =
            [
                .. FieldValidator.CorrectQuantities(type, legacyFields),
                .. FieldValidator.Validate(type, legacyFields),
                .. fromImage ? [] : FieldValidator.CheckGrounded(type, legacyFields, ocr),
            ];
            var engineFields = JsonNode.Parse(json)!.AsObject();
            var engine = Validator.Validate(Pack(type), engineFields, ocr, fromImage);

            Assert.Equal(legacy, engine);
            Assert.Equal(legacyFields.ToJsonString(), engineFields.ToJsonString());  // 보정 결과도 같음
            Assert.NotEmpty(legacy);  // 예시가 실제로 규칙에 걸려야 비교가 의미 있음
        }
    }

    [Fact]
    public void 영수증_예시는_보정과_근거확인이_실제로_걸린다()
    {
        var issues = Validator.Validate(Pack(DocumentTypes.Receipt), JsonNode.Parse(ReceiptJson)!.AsObject(), ReceiptOcr);
        Assert.Contains(issues, i => i.Rule == "item_qty_corrected");
        Assert.Contains(issues, i => i.Rule == "grounded");
    }

    [Fact]
    public void 모든_팩은_등록된_규칙만_쓴다()
    {
        foreach (var dir in Directory.GetDirectories(Path.Combine(Repo, "packs")))
        {
            var pack = DocumentType.Load(dir);  // 모르는 규칙이면 여기서 예외
            Assert.All(pack.Rules, r => Assert.True(RuleRegistry.IsKnown(r)));
        }
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
