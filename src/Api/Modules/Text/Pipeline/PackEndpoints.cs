using System.Text.Json.Nodes;
using Digitizer.Engine;
using Digitizer.Engine.Rules;

namespace Api.Modules.Text.Pipeline;

/// <summary>팩 관리 화면: 상세 · 편집 도구 목록(필드 타입 · 규칙) · 추가 · 고치기. 저장 규칙은 PackStore.Save</summary>
public static class PackEndpoints
{
    public sealed record FieldTypeDto(string Type, string Description);
    public sealed record RuleDto(string Name, string Kind, string? Description);
    public sealed record PackMetaDto(IReadOnlyList<FieldTypeDto> FieldTypes, IReadOnlyList<RuleDto> Rules);

    /// <summary>
    /// Managed = 프롬프트 관리에 등록된 종류 (영수증 · 상업송장 · 보험 청구서). 문서 처리는 지시문을 프롬프트 관리의 적용 버전으로 쓰고,
    /// 팩 파일은 평가 도구 · exe 와 빌드 때 프롬프트 기본값으로 쓰임
    /// </summary>
    public sealed record PackDetailDto(
        PackDto Summary,
        JsonObject Type,
        Dictionary<string, string> Files,
        bool Managed,
        IReadOnlyList<PackStore.HistoryEntry> History);

    public sealed record SavePackRequest(JsonObject Type, Dictionary<string, string?>? Files, string? Note);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/meta", () => new PackMetaDto(
            [.. PackCheck.FieldTypes.Select(t => new FieldTypeDto(t.Key, t.Value))],
            [.. RuleRegistry.Names.Select(r => new RuleDto(r, RuleRegistry.Kind(r), RuleRegistry.Descriptions.GetValueOrDefault(r)))]));

        group.MapGet("/{id}", (string id, PackStore packs, TextPrompts prompts) =>
            packs.Get(id) is { } pack && packs.TypeJson(id) is { } type
                ? Results.Ok(new PackDetailDto(PackDto.From(pack), type, packs.Files(id), prompts.Has($"extract.{id}"), packs.History(id)))
                : Results.NotFound());

        group.MapPost("", (SavePackRequest request, PackStore packs) =>
        {
            var id = request.Type["id"]?.GetValue<string>() ?? "";
            return Result(packs.Save(id, request.Type, request.Files ?? [], request.Note, create: true), packs, id);
        });

        group.MapPut("/{id}", (string id, SavePackRequest request, PackStore packs) =>
            Result(packs.Save(id, request.Type, request.Files ?? [], request.Note, create: false), packs, id));
    }

    private static IResult Result(List<string> errors, PackStore packs, string id) => errors.Count == 0
        ? Results.Ok(PackDto.From(packs.Get(id)!))
        // 문제는 한 줄에 하나 (화면이 줄 단위로 목록 표시)
        : Results.Problem(string.Join("\n", errors), statusCode: StatusCodes.Status400BadRequest, title: "팩을 저장하지 못했습니다");
}
