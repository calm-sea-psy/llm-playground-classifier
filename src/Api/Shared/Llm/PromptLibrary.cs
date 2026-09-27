using System.Collections.Concurrent;
using Api.Shared.Prompts;
using Microsoft.SemanticKernel;

namespace Api.Shared.Llm;

/// <summary>
/// 모듈의 프롬프트를 SK 프롬프트 템플릿({{$변수}})으로 렌더링한다. 모듈마다 모듈 키만 다른 하위 클래스를 둔다.
/// 내용은 PromptStore 에서: UI 에서 적용한 버전(DB)이 있으면 그것, 없으면 파일(Modules/{모듈}/Pipeline/Prompts/*.md)
/// 렌더링한 프롬프트의 버전은 PromptUsage 로 작업에 기록.
/// 모델별 프롬프트: {이름}.{모델 계열} 이 있으면 그것을 씀 (예: extract.receipt.qwen3-vl, 모델 계열 = 태그의 ':' 앞)
/// </summary>
public abstract class PromptLibrary(Kernel kernel, PromptStore store, PromptUsage usage, string module)
{
    /// <summary>내용 ➔ 해석한 템플릿. 버전을 바꾸면 내용이 달라져 새로 해석됨 (값을 그대로 넣는지에 따라 따로)</summary>
    private static readonly ConcurrentDictionary<(string, bool), IPromptTemplate> Cache = new();
    private static readonly KernelPromptTemplateFactory Factory = new();
    private static readonly KernelPromptTemplateFactory RawFactory = new() { AllowDangerouslySetContent = true };

    /// <summary>
    /// 변수 값({{$ocr_text}} 등)을 그대로 넣을지. SK 는 기본으로 값의 &lt; &gt; &amp; " ' · 를 HTML 기호(&amp;quot; 등)로 바꿈
    /// (PromptTemplateConfig.AllowDangerouslySetContent 는 함수 반환값에만 적용되고 변수에는 안 됨 ➔ 4차-exe 6-1 에서 발견).
    /// 문서 처리(Text)는 true: 설치판(Engine PackPromptSource)과 같은 문장을 보내야 측정한 결과가 배포판에도 맞음.
    /// 이미지 · 통합 모듈은 측정 때의 동작(기호로 바꿈)을 유지 (바꾸면 그 측정을 다시 해야 함)
    /// </summary>
    protected virtual bool RawValues => false;

    /// <summary>프롬프트 저장소(파일 또는 화면에서 저장한 버전)에 있는지</summary>
    public bool Has(string name) => store.Exists(module, name);

    public Task<string> RenderAsync(string name, KernelArguments? arguments = null, CancellationToken ct = default) =>
        RenderTemplateAsync(name, arguments, ct);

    /// <summary>모델 전용 프롬프트가 있으면 그것을, 없으면 공용 프롬프트를 렌더링</summary>
    public Task<string> RenderForModelAsync(string name, string model, KernelArguments? arguments = null, CancellationToken ct = default)
    {
        var specific = $"{name}.{ModelFamily(model)}";
        return RenderTemplateAsync(store.Exists(module, specific) ? specific : name, arguments, ct);
    }

    /// <summary>프롬프트 파일 이름으로 쓸 모델 계열: "qwen3-vl:8b" ➔ "qwen3-vl"</summary>
    public static string ModelFamily(string model) => model.Split(':')[0];

    private Task<string> RenderTemplateAsync(string name, KernelArguments? arguments, CancellationToken ct)
    {
        var (content, version) = store.Resolve(module, name);
        if (content is null)
        {
            throw new FileNotFoundException($"프롬프트 '{module}/{name}' 이(가) 없습니다");
        }
        usage.Record(module, name, version, content); // 작업 기록 (jobs.prompts)
        var template = Cache.GetOrAdd((content, RawValues), key => (key.Item2 ? RawFactory : Factory).Create(new PromptTemplateConfig(key.Item1)
        {
            // 함수 반환값을 그대로 (변수 값은 RawValues 가 정함)
            AllowDangerouslySetContent = true,
        }));
        return template.RenderAsync(kernel, arguments ?? [], ct);
    }
}
