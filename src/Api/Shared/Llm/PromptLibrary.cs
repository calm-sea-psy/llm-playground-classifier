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
    /// <summary>내용 ➔ 해석한 템플릿. 버전을 바꾸면 내용이 달라져 새로 해석됨</summary>
    private static readonly ConcurrentDictionary<string, IPromptTemplate> Cache = new();
    private static readonly KernelPromptTemplateFactory Factory = new();

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
        var template = Cache.GetOrAdd(content, c => Factory.Create(new PromptTemplateConfig(c)
        {
            // 렌더링 결과를 채팅 메시지로 다시 파싱하지 않으므로 OCR 텍스트의 <, & 를 이스케이프하지 않음
            AllowDangerouslySetContent = true,
        }));
        return template.RenderAsync(kernel, arguments ?? [], ct);
    }
}
