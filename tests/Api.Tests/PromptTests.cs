using Api.Modules.Multimodal;
using Api.Modules.Multimodal.Pipeline;
using Api.Shared.Prompts;

namespace Api.Tests;

/// <summary>
/// 프롬프트 관리: 저장 전 검사, 모델 전용 이름, 작업 기록, 용어집(RAG) 검색.
/// 파일 기본값은 API 프로젝트가 출력 폴더로 복사한 Modules/*/Pipeline/Prompts 를 그대로 읽는다
/// </summary>
public class PromptTests
{
    // DB 를 쓰지 않는 메서드만 테스트하므로 스코프 팩토리는 필요 없음
    private static readonly PromptStore Store = new(null!, TimeProvider.System);
    private static readonly string[] Families = ["gemma4", "qwen3-vl"];

    [Fact]
    public void 파일_기본값을_읽는다()
    {
        Assert.Contains("{{$report}}", Store.FileContent("multimodal", "summarize.user", PromptStore.Markdown));
        Assert.Null(Store.FileContent("multimodal", "없는-프롬프트", PromptStore.Markdown));
    }

    [Fact]
    public void 템플릿_변수를_뽑는다()
    {
        Assert.Equal(["input_label", "ocr_text"], PromptStore.Variables("{{$ocr_text}} / {{ $input_label }} / {{$ocr_text}}"));
    }

    [Theory]
    [InlineData("text", "extract.receipt.qwen3-vl", "extract.receipt", "qwen3-vl")]
    [InlineData("multimodal", "summarize.system.gemma4", "summarize.system", "gemma4")]
    [InlineData("text", "extract.vlm.user", "extract.vlm.user", null)] // extract.receipt 등의 변형이 아님
    [InlineData("multimodal", "summarize.user.gemma4", "summarize.user.gemma4", null)] // user 프롬프트는 모델 전용 불가
    public void 모델_전용_이름을_나눈다(string module, string name, string baseName, string? family)
    {
        Assert.Equal((baseName, family), PromptStore.SplitVariant(module, name));
    }

    [Fact]
    public void 기본값과_변수가_같으면_통과()
    {
        Assert.Null(Store.Validate("multimodal", "summarize.user", PromptStore.Markdown, "소견서:\n{{$report}}", Families));
    }

    [Fact]
    public void 필요한_변수가_빠지면_거부()
    {
        var error = Store.Validate("multimodal", "summarize.user", PromptStore.Markdown, "소견서 없음", Families);
        Assert.Contains("빠졌습니다", error);
        Assert.Contains("{{$report}}", error);
    }

    [Fact]
    public void 파이프라인이_넘기지_않는_변수는_거부()
    {
        var error = Store.Validate("multimodal", "summarize.user", PromptStore.Markdown, "{{$report}} {{$foo}}", Families);
        Assert.Contains("{{$foo}}", error);
    }

    [Fact]
    public void 파이프라인이_쓰지_않는_이름은_거부()
    {
        Assert.NotNull(Store.Validate("multimodal", "hello", PromptStore.Markdown, "x", Families));
    }

    [Fact]
    public void 모델_전용_변형은_설정된_모델_계열만()
    {
        Assert.Null(Store.Validate("multimodal", "summarize.system.qwen3-vl", PromptStore.Markdown, "지시문", Families));
        Assert.Contains("llama", Store.Validate("multimodal", "summarize.system.llama", PromptStore.Markdown, "지시문", Families));
    }

    [Theory]
    [InlineData("""{ "entries": [ { "pattern": "(abc", "text": "t" } ] }""", "정규식")]
    [InlineData("""{ "entries": [ { "pattern": "abc" } ] }""", "pattern·text")]
    [InlineData("""{ "items": [] }""", "entries")]
    [InlineData("""{ not json""", "JSON")]
    public void 용어집_형식_검사(string content, string expected)
    {
        Assert.Contains(expected, Store.Validate("multimodal", ReportGlossary.Name, PromptStore.Json, content, Families));
    }

    [Theory]
    [InlineData("summarize.system.qwen3-vl", true)]
    [InlineData("Summarize", false)]
    [InlineData("../etc", false)]
    public void 이름_규칙(string name, bool valid)
    {
        Assert.Equal(valid, PromptStore.IsValidName(name));
    }

    [Fact]
    public void 작업_기록은_버전과_내용_해시를_남긴다()
    {
        var usage = new PromptUsage();
        Assert.Null(usage.ToJson());

        usage.Record("text", "extract.receipt", 2, "내용 A");
        usage.Record("text", "classify.user", null, "내용 B");
        var parsed = PromptUsage.Parse(usage.ToJson());

        Assert.Equal(2, parsed["text/extract.receipt"].Version);
        Assert.Null(parsed["text/classify.user"].Version);
        Assert.Equal(8, parsed["text/classify.user"].Hash.Length);
    }

    [Fact]
    public void 실험_도중_바뀐_프롬프트만_경고()
    {
        string Job(int? version, string content, string name = "extract.receipt")
        {
            var u = new PromptUsage();
            u.Record("text", name, version, content);
            u.Record("text", "classify.user", null, "같은 내용");
            return u.ToJson()!;
        }

        var mixes = PromptUsage.Mixed([Job(null, "기본"), Job(1, "수정"), Job(null, "기본"), null]);
        var mix = Assert.Single(mixes);
        Assert.Equal("text/extract.receipt", mix.Prompt);
        Assert.Equal(2, mix.Versions.Count);

        // 조합마다 모델이 달라 모델 전용 프롬프트를 쓴 것은 이름이 달라 경고 대상 아님
        Assert.Empty(PromptUsage.Mixed([Job(null, "기본"), Job(null, "qwen 전용", "extract.receipt.qwen3-vl")]));
    }

    [Fact]
    public void 용어집은_소견서에_나온_용어의_정의만_찾는다()
    {
        var usage = new PromptUsage();
        var glossary = new ReportGlossary(Store, usage);

        var hits = glossary.Retrieve("IMPRESSION: Cardiomegaly and pulmonary vascular congestion.");
        Assert.Contains(hits, h => h.StartsWith("pulmonary vascular congestion"));
        Assert.DoesNotContain(hits, h => h.StartsWith("pneumothorax"));
        Assert.Contains($"{MultimodalModule.ModuleKey}/{ReportGlossary.Name}", PromptUsage.Parse(usage.ToJson()).Keys);

        Assert.Equal("", ReportGlossary.Block([]));
        Assert.StartsWith("\n\n참고 용어 정의", ReportGlossary.Block(hits));
    }
}
