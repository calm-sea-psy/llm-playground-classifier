using Api.Shared.Llm;
using Api.Shared.Prompts;
using Microsoft.SemanticKernel;

namespace Api.Modules.Text.Pipeline;

public sealed class TextPrompts(Kernel kernel, PromptStore store, PromptUsage usage) : PromptLibrary(kernel, store, usage, TextModule.ModuleKey);
