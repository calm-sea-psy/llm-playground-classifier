using Api.Shared.Llm;
using Api.Shared.Prompts;
using Microsoft.SemanticKernel;

namespace Api.Modules.Text.Pipeline;

/// <summary>문서 처리 프롬프트. OCR 원문 · 팩 변수를 그대로 넣음 (설치판과 같은 문장, PromptLibrary.RawValues)</summary>
public sealed class TextPrompts(Kernel kernel, PromptStore store, PromptUsage usage) : PromptLibrary(kernel, store, usage, TextModule.ModuleKey)
{
    protected override bool RawValues => true;
}
