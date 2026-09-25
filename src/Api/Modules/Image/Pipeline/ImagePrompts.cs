using Api.Shared.Llm;
using Api.Shared.Prompts;
using Microsoft.SemanticKernel;

namespace Api.Modules.Image.Pipeline;

public sealed class ImagePrompts(Kernel kernel, PromptStore store, PromptUsage usage) : PromptLibrary(kernel, store, usage, ImageModule.ModuleKey);
