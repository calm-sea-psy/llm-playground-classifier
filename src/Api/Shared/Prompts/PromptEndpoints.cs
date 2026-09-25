using Api.Shared.Data;
using Api.Shared.Features;
using Api.Shared.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Shared.Prompts;

public sealed record SavePromptRequest(string Content, string? Note, bool Activate = true);

/// <summary>
/// /api/prompts: 파이프라인 프롬프트 목록·내용 확인, 새 버전 저장·적용·파일 기본값으로 되돌리기·버전 삭제.
/// 적용한 내용은 재시작 없이 다음 작업부터 쓰인다 (이미 끝난 작업·실행 중인 작업은 그대로)
/// </summary>
public static class PromptEndpoints
{
    public static void MapPromptEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/prompts");

        // 켜진 모듈의 프롬프트 목록 + 모델 전용 변형을 만들 때 고를 모델 계열
        group.MapGet("", async (FeatureRegistry features, PromptStore store, IOptions<LlmOptions> llm, AppDbContext db, CancellationToken ct) =>
            Results.Ok(new
            {
                Modules = features.EnabledModules.Select(m => new { m.Key, m.DisplayName }),
                ModelFamilies = Families(llm.Value),
                Prompts = await store.ListAsync(features.EnabledModules.Select(m => m.Key), db, ct),
            }));

        // 프롬프트 1개: 설명 + 지금 쓰는 내용 + 파일 기본값 + 버전 목록(내용 포함)
        group.MapGet("/{module}/{name}", async (string module, string name, FeatureRegistry features, PromptStore store,
            AppDbContext db, CancellationToken ct) =>
        {
            if (!IsEnabled(features, module))
            {
                return Results.NotFound();
            }
            var kind = store.KindOf(module, name);
            var versions = await db.Set<PromptVersion>().AsNoTracking()
                .Where(p => p.Module == module && p.Name == name)
                .OrderByDescending(p => p.Version)
                .ToListAsync(ct);
            var fileContent = store.FileContent(module, name, kind);
            var (baseName, family) = PromptStore.SplitVariant(module, name);
            if (fileContent is null && versions.Count == 0)
            {
                return Results.NotFound();
            }
            var activeVersion = versions.FirstOrDefault(v => v.Active);
            return Results.Ok(new
            {
                Info = store.Describe(module, name, kind, activeVersion?.Version, versions.Count, versions.FirstOrDefault()?.CreatedAt),
                Content = store.Content(module, name, kind),
                FileContent = fileContent,
                // 모델 전용 변형은 공용 프롬프트와 비교
                BaseName = family is null ? null : baseName,
                BaseContent = family is null ? null : store.Content(module, baseName, kind),
                Versions = versions.Select(v => new { v.Id, v.Version, v.Content, v.Note, v.Active, v.CreatedAt }),
            });
        });

        // 새 버전 저장 (activate 면 바로 적용). 파일에 없는 이름은 모델 전용 변형({공용 이름}.{모델 계열})만 허용
        group.MapPost("/{module}/{name}/versions", async (string module, string name, SavePromptRequest request,
            FeatureRegistry features, PromptStore store, IOptions<LlmOptions> llm, AppDbContext db, CancellationToken ct) =>
        {
            if (!IsEnabled(features, module))
            {
                return Results.NotFound();
            }
            if (!PromptStore.IsValidName(name))
            {
                return Results.Problem("이름은 영문 소문자·숫자·._- 만 쓸 수 있습니다", statusCode: StatusCodes.Status400BadRequest);
            }
            var kind = store.KindOf(module, name);
            if (store.Validate(module, name, kind, request.Content ?? "", Families(llm.Value)) is { } error)
            {
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
            }
            var active = store.Content(module, name, kind);
            if (request.Activate && active == request.Content)
            {
                return Results.Problem("지금 쓰는 내용과 같습니다", statusCode: StatusCodes.Status400BadRequest);
            }
            var saved = await store.SaveAsync(db, module, name, request.Content!, request.Note, request.Activate, ct);
            return Results.Ok(new { saved.Id, saved.Version });
        });

        // 버전 적용
        group.MapPost("/{module}/{name}/versions/{id:long}/activate", async (string module, string name, long id,
            PromptStore store, AppDbContext db, CancellationToken ct) =>
        {
            if (!await db.Set<PromptVersion>().AnyAsync(p => p.Id == id && p.Module == module && p.Name == name, ct))
            {
                return Results.NotFound();
            }
            await store.ActivateAsync(db, module, name, id, ct);
            return Results.NoContent();
        });

        // 파일 기본값으로 되돌리기 (버전은 남김). 파일이 없는 모델 전용 변형이면 = 공용 프롬프트를 쓰게 됨
        group.MapPost("/{module}/{name}/reset", async (string module, string name, PromptStore store, AppDbContext db,
            CancellationToken ct) =>
        {
            await store.ActivateAsync(db, module, name, null, ct);
            return Results.NoContent();
        });

        // 적용 중이 아닌 버전 삭제
        group.MapDelete("/{module}/{name}/versions/{id:long}", async (string module, string name, long id,
            AppDbContext db, CancellationToken ct) =>
        {
            var v = await db.Set<PromptVersion>().FirstOrDefaultAsync(p => p.Id == id && p.Module == module && p.Name == name, ct);
            if (v is null)
            {
                return Results.NotFound();
            }
            if (v.Active)
            {
                return Results.Problem("적용 중인 버전은 삭제할 수 없습니다. 다른 버전을 적용하거나 기본값으로 되돌린 뒤 삭제하세요",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            db.Remove(v);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static bool IsEnabled(FeatureRegistry features, string module) =>
        features.EnabledModules.Any(m => m.Key == module);

    /// <summary>설정된 모델의 계열 ("qwen3-vl:8b-instruct" ➔ "qwen3-vl")</summary>
    private static List<string> Families(LlmOptions llm) =>
        llm.Models.Select(PromptLibrary.ModelFamily).Distinct().ToList();
}
