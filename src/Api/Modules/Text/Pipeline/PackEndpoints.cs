using System.Text.Json.Nodes;
using Api.Shared.Data;
using Api.Shared.Prompts;
using Digitizer.Engine;
using Digitizer.Engine.Rules;
using Microsoft.EntityFrameworkCore;

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
        IReadOnlyList<PackStore.HistoryEntry> History,
        IReadOnlyList<PromptOverride> PromptOverrides);

    /// <summary>프롬프트 관리에서 적용 중인 DB 버전. 문서 처리 · 모델 비교는 이 문장을 쓰지만 exe 는 팩 파일을 씀 ➔ 합격 표시 전에 팩에 반영하거나 되돌려야 함</summary>
    public sealed record PromptOverride(string Name, int Version);

    public sealed record SavePackRequest(JsonObject Type, Dictionary<string, string?>? Files, string? Note);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/meta", () => new PackMetaDto(
            [.. PackCheck.FieldTypes.Select(t => new FieldTypeDto(t.Key, t.Value))],
            [.. RuleRegistry.Names.Select(r => new RuleDto(r, RuleRegistry.Kind(r), RuleRegistry.Descriptions.GetValueOrDefault(r)))]));

        group.MapGet("/{id}", async (string id, PackStore packs, TextPrompts prompts, AppDbContext db, CancellationToken ct) =>
        {
            if (packs.Get(id) is not { } pack || packs.TypeJson(id) is not { } type) return Results.NotFound();
            var managed = prompts.Has($"extract.{id}");
            var overrides = managed ? await OverridesAsync(db, id, ct) : [];
            return Results.Ok(new PackDetailDto(PackDto.From(pack), type, packs.Files(id), managed, packs.History(id), overrides));
        });

        group.MapPost("", (SavePackRequest request, PackStore packs) =>
        {
            var id = request.Type["id"]?.GetValue<string>() ?? "";
            return Result(packs.Save(id, request.Type, request.Files ?? [], request.Note, create: true), packs, id);
        });

        group.MapPut("/{id}", (string id, SavePackRequest request, PackStore packs) =>
            Result(packs.Save(id, request.Type, request.Files ?? [], request.Note, create: false), packs, id));
    }

    /// <summary>이 팩이 문서 처리에서 쓰는 프롬프트(extract.{id} · 모델 전용 · 공용 user 틀) 중 DB 버전이 적용 중인 것</summary>
    private static async Task<List<PromptOverride>> OverridesAsync(AppDbContext db, string id, CancellationToken ct) =>
        await db.Set<PromptVersion>()
            .Where(p => p.Module == TextModule.ModuleKey && p.Active
                && (p.Name == $"extract.{id}" || p.Name.StartsWith($"extract.{id}.") || p.Name == "extract.user" || p.Name == "extract.vlm.user"))
            .OrderBy(p => p.Name)
            .Select(p => new PromptOverride(p.Name, p.Version))
            .ToListAsync(ct);

    private static IResult Result(List<string> errors, PackStore packs, string id) => errors.Count == 0
        ? Results.Ok(PackDto.From(packs.Get(id)!))
        // 문제는 한 줄에 하나 (화면이 줄 단위로 목록 표시)
        : Results.Problem(string.Join("\n", errors), statusCode: StatusCodes.Status400BadRequest, title: "팩을 저장하지 못했습니다");
}
