using System.Text.Json.Nodes;
using Digitizer.Engine;
using Digitizer.Engine.Rules;

namespace Api.Tests;

/// <summary>
/// Engine DocumentProcessor (평가 도구 · exe 공용 흐름): 폴백 조건 · 최종 선택 · 넘기는 문장.
/// 4차-exe 1단계에서 Api TextJobHandler 의 판단을 옮긴 것이라 기존 동작과 같아야 함
/// </summary>
public class DocumentProcessorTests
{
    private static readonly string Repo = FindRepo();

    /// <summary>검증 규칙을 끈 영수증 팩: 필드가 모두 있으면 통과, 빠지면 "필드 누락" 오류 1건 (흐름만 시험)</summary>
    private static readonly DocumentType Receipt = DocumentType.Load(Path.Combine(Repo, "packs", "receipt")) with { Rules = [], GenericChecks = false };
    private static readonly DocumentType Resume = DocumentType.Load(Path.Combine(Repo, "packs", "resume"));

    private static JsonObject Complete(DocumentType pack) => new(pack.Fields.Select(f => KeyValuePair.Create(f.Name, (JsonNode?)null)));
    private static readonly JsonObject Incomplete = [];

    private sealed class FakeExtractor(JsonObject? text, JsonObject? vlm) : IFieldExtractor
    {
        public List<(string System, string User, bool Image)> Calls { get; } = [];

        public Task<Extraction> ExtractMessagesAsync(DocumentType type, string system, string user, IReadOnlyList<ImageInput>? images, CancellationToken ct = default)
        {
            Calls.Add((system, user, images is { Count: > 0 }));
            var fields = images is { Count: > 0 } ? vlm : text;
            return Task.FromResult(new Extraction(fields?.DeepClone().AsObject(), "{}", 10, 1, 1, 1, fields is null ? "JSON 형식 오류" : null));
        }
    }

    private static (DocumentProcessor Processor, FakeExtractor Fake, List<(ProcessStage, string)> Status) Make(JsonObject? text, JsonObject? vlm)
    {
        var fake = new FakeExtractor(text, vlm);
        var status = new List<(ProcessStage, string)>();
        var processor = new DocumentProcessor(_ => fake, PackPromptSource.Instance)
        {
            OnStatus = (stage, message, _) => { status.Add((stage, message)); return Task.CompletedTask; },
        };
        return (processor, fake, status);
    }

    private static readonly Func<ImageInput> Image = () => new ImageInput([1, 2, 3], "image/jpeg");
    private static ProcessInput Input(string text, double? confidence = 0.95, bool image = true) =>
        new(new DocumentText(text, Structured: false), confidence, image ? Image : null);
    private static readonly ProcessOptions Options = new("gemma4:12b", VlmFallback: true, FallbackConfidence: 0.9);

    [Fact]
    public async Task 텍스트_추출이_통과하면_폴백하지_않는다()
    {
        var (processor, fake, status) = Make(Complete(Receipt), Complete(Receipt));
        var r = await processor.ProcessAsync(Receipt, Input("합계 1000"), Options);

        Assert.Equal(ExtractionAttempt.Text, r.Final!.Source);
        Assert.Null(r.FallbackReason);
        Assert.Single(fake.Calls);
        Assert.Equal([ProcessStage.Extracting, ProcessStage.Validated], status.Select(s => s.Item1));
        Assert.Equal("receipt@1.0.0", r.Final.Engine);
    }

    [Fact]
    public async Task 검증_오류면_폴백하고_통과한_VLM_결과를_쓴다()
    {
        var (processor, fake, status) = Make(Incomplete, Complete(Receipt));
        var r = await processor.ProcessAsync(Receipt, Input("합계 1000"), Options);

        Assert.Equal(ExtractionAttempt.Vlm, r.Final!.Source);
        Assert.Equal("검증 오류 1건", r.FallbackReason);
        Assert.True(fake.Calls[1].Image);
        Assert.DoesNotContain("규칙으로 검사했더니", fake.Calls[1].User);  // 필드 누락은 형식 오류라 힌트에 안 들어감 (기존과 같음)
        Assert.Equal("VLM 폴백 추출 중 (검증 오류 1건)", status[2].Item2);
    }

    [Fact]
    public void 폴백_힌트에는_검증_오류만_들어간다()
    {
        var hint = DocumentProcessor.IssueHint([
            new ValidationIssue("items_sum", "items", IssueSeverity.Error, "품목 합 13,860 ≠ 합계 12,000"),
            new ValidationIssue("gross_total", "gross_total", IssueSeverity.Warning, "경고는 빠짐"),
        ]);
        Assert.Contains("- 품목 합 13,860 ≠ 합계 12,000", hint);
        Assert.DoesNotContain("경고는 빠짐", hint);
        Assert.Contains("값을 계산하거나 바꾸지 마세요", hint);
        Assert.Equal("", DocumentProcessor.IssueHint([]));
    }

    [Fact]
    public async Task OCR_신뢰도가_낮으면_통과해도_폴백한다()
    {
        var (processor, _, _) = Make(Complete(Receipt), Complete(Receipt));
        var r = await processor.ProcessAsync(Receipt, Input("합계 1000", confidence: 0.5), Options);

        Assert.Equal("OCR 평균 신뢰도 0.50 < 0.90", r.FallbackReason);
        Assert.Equal(ExtractionAttempt.Vlm, r.Final!.Source);  // 둘 다 통과면 VLM
    }

    [Fact]
    public async Task 이미지가_없거나_폴백_틀이_없거나_폴백을_끄면_폴백하지_않는다()
    {
        var (p1, f1, _) = Make(Incomplete, Complete(Receipt));
        Assert.Null((await p1.ProcessAsync(Receipt, Input("합계", image: false), Options)).FallbackReason);  // PDF · DOCX
        Assert.Single(f1.Calls);

        var (p2, f2, _) = Make(Incomplete, Complete(Resume));
        Assert.Null((await p2.ProcessAsync(Resume, Input("이름"), Options)).FallbackReason);  // 이력서 팩은 vlm.user.md 없음
        Assert.Single(f2.Calls);

        var (p3, f3, _) = Make(Incomplete, Complete(Receipt));
        Assert.Null((await p3.ProcessAsync(Receipt, Input("합계"), Options with { VlmFallback = false })).FallbackReason);
        Assert.Single(f3.Calls);
    }

    [Fact]
    public async Task 원문이_비면_이미지로만_추출하고_이미지도_없으면_결과가_없다()
    {
        var (p1, f1, _) = Make(Complete(Receipt), Complete(Receipt));
        var r1 = await p1.ProcessAsync(Receipt, Input("", confidence: null), Options);
        Assert.Equal("OCR 텍스트 없음", r1.FallbackReason);
        Assert.Equal(ExtractionAttempt.Vlm, r1.Final!.Source);
        Assert.Single(f1.Calls);

        var (p2, _, _) = Make(Complete(Receipt), Complete(Receipt));
        var r2 = await p2.ProcessAsync(Receipt, Input("", confidence: null, image: false), Options);
        Assert.Null(r2.Final);
        Assert.Empty(r2.Attempts);
    }

    [Fact]
    public async Task 둘_다_실패하면_오류가_적은_쪽_같으면_텍스트()
    {
        var (processor, _, _) = Make(Incomplete, Incomplete);
        var r = await processor.ProcessAsync(Receipt, Input("합계"), Options);
        Assert.Equal(ExtractionAttempt.Text, r.Final!.Source);
        Assert.Equal(2, r.Attempts.Count);
    }

    [Fact]
    public async Task 팩_문장은_기존_측정_경로와_같다()
    {
        // 이력서 0단계 측정(FieldExtractor.ExtractAsync)이 만들던 지시문 · 메시지와 같아야 측정 결과가 유지됨
        var (processor, fake, _) = Make(Complete(Resume), null);
        var document = new DocumentText("홍길동\n010-1234-5678", Structured: false, FieldExtractor.InputLabel("pdf-text"));
        await processor.ProcessAsync(Resume, new ProcessInput(document, null, null), Options);

        Assert.Equal(Resume.SystemPromptFor("gemma4:12b"), fake.Calls[0].System);
        Assert.Equal(Resume.UserMessage(FieldExtractor.InputLabel("pdf-text"), document.Text), fake.Calls[0].User);
    }

    [Fact]
    public void 읽기_순서는_위에서_아래_같은_줄은_왼쪽부터()
    {
        int[][] Box(int x, int y, int w = 50, int h = 20) => [[x, y], [x + w, y], [x + w, y + h], [x, y + h]];
        var text = ReadingOrder.Build([
            new OcrBox("합계", Box(10, 100)),
            new OcrBox("1,000", Box(200, 104)),   // 같은 줄 (세로 중앙보다 위에서 시작)
            new OcrBox("상호", Box(10, 10)),
            new OcrBox("좌표 없음", null),
        ]);
        Assert.Equal("상호\n합계 1,000\n좌표 없음", text);
    }

    [Fact]
    public void 세로_중심_방식은_높은_칸_옆의_낮은_칸도_한_행으로_묶는다()
    {
        int[][] Box(int x, int top, int bottom) => [[x, top], [x + 50, top], [x + 50, bottom], [x, bottom]];
        OcrBox[] cells = [
            new("서울특별시 강남구", Box(100, 80, 140)),  // 칸 안에서 여러 줄이라 높은 상자
            new("112동", Box(300, 118, 138)),             // 옆 칸, 높은 상자의 세로 중앙보다 아래에서 시작
        ];
        Assert.Equal("서울특별시 강남구\n112동", ReadingOrder.Build(cells, ReadingOrder.Top));
        Assert.Equal("서울특별시 강남구  112동", ReadingOrder.Build(cells, ReadingOrder.Center));
    }

    [Fact]
    public void 팩마다_측정한_줄_순서_방식을_쓴다()
    {
        // 영수증은 KORIE 150장을 top 으로, 이력서는 0단계를 center 로 측정 (4차-exe 1단계: 하나로 합치면 이력서 스캔 결과가 바뀜)
        Assert.Equal(ReadingOrder.Top, DocumentType.Load(Path.Combine(Repo, "packs", "receipt")).ReadingOrder);
        Assert.Equal(ReadingOrder.Center, Resume.ReadingOrder);
        Assert.Contains(PackCheck.Check(Resume with { ReadingOrder = "left" }), e => e.Contains("reading_order"));
    }

    private static string FindRepo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "llm-playground-classifier.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("저장소 루트를 찾지 못했습니다");
    }
}
