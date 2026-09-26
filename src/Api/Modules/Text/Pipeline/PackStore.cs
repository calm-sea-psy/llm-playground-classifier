using System.Collections.Concurrent;
using Digitizer.Engine;
using Microsoft.Extensions.Options;

namespace Api.Modules.Text.Pipeline;

/// <summary>추출 방식: legacy = 기존 Text 모듈 (SK 커넥터 + FieldValidator 직접 호출), engine = Digitizer.Engine + 문서 종류 팩</summary>
public static class ExtractionModes
{
    public const string Legacy = "legacy";
    public const string Engine = "engine";
    public static readonly string[] All = [Legacy, Engine];
}

/// <summary>문서 종류 팩 (packs/{문서 종류}/) 을 읽어 둠. 팩이 없는 종류는 null ➔ legacy 로 처리</summary>
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
