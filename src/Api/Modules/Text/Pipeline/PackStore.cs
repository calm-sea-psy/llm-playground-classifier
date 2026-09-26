using System.Collections.Concurrent;
using Digitizer.Engine;
using Microsoft.Extensions.Options;

namespace Api.Modules.Text.Pipeline;

/// <summary>문서 종류 팩 (packs/{문서 종류}/) 을 읽어 둠. 팩이 없는 종류는 null (필드 추출 불가)</summary>
public sealed class PackStore(IWebHostEnvironment env, IOptions<PipelineOptions> options)
{
    private readonly ConcurrentDictionary<string, DocumentType?> _packs = new();

    public string Root => Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.PacksRoot));

    public DocumentType? Get(string documentType) => _packs.GetOrAdd(documentType, id =>
    {
        var dir = Path.Combine(Root, id);
        return Directory.Exists(dir) ? DocumentType.Load(dir) : null;
    });
}
