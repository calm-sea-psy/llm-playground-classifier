using Api.Shared.Data;
using Api.Shared.Storage;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace Api.Shared.Jobs;

/// <summary>업로드 파일 ➔ Job 생성 (모듈 공통: 단건 업로드와 모델 비교 실험이 같이 사용)</summary>
public sealed class JobFactory(
    AppDbContext db,
    UploadStorage storage,
    JobQueue queue,
    JobReporter reporter,
    IOptions<StorageOptions> storageOptions,
    TimeProvider clock)
{
    public static readonly HashSet<string> AllowedExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp"];

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>문제가 있으면 사유, 없으면 null</summary>
    public string? Validate(IFormFile file)
    {
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            return $"지원하지 않는 형식입니다: {file.FileName} (지원: {string.Join(", ", AllowedExtensions)})";
        }
        var maxMb = storageOptions.Value.MaxUploadMb;
        return file.Length == 0 || file.Length > maxMb * 1024L * 1024L
            ? $"파일 크기는 0 초과 {maxMb}MB 이하여야 합니다: {file.FileName}"
            : null;
    }

    /// <summary>원본을 저장하고 Job 을 DB 에 추가 (SaveChanges 는 호출 측에서)</summary>
    /// <param name="jobType">모듈 키 (IPipelineModule.Key)</param>
    /// <param name="settingsJson">모듈이 해석하는 설정 스냅숏 JSON</param>
    public async Task<Job> CreateAsync(
        IFormFile file, string jobType, string? model, string? settingsJson, Guid? experimentId, int? comboIndex,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        var job = new Job
        {
            Id = id,
            JobType = jobType,
            Model = model,
            Settings = settingsJson,
            ExperimentId = experimentId,
            ComboIndex = comboIndex,
            StatusMessage = experimentId is null ? "업로드 완료" : $"실험 대기 (조합 {comboIndex + 1})",
            FileName = Path.GetFileName(file.FileName),
            StoredFileName = await storage.SaveOriginalAsync(id, file, ct),
            // 클라이언트가 보낸 Content-Type 대신 확장자로 결정 (결과 화면에서 이미지로 표시되도록)
            ContentType = ContentTypes.TryGetContentType(file.FileName, out var contentType) ? contentType : "application/octet-stream",
            FileSize = file.Length,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Jobs.Add(job);
        return job;
    }

    /// <summary>저장된 Job 들을 큐에 넣음. 큐가 가득 차면 해당 Job 은 실패 처리하고 false</summary>
    public async Task<bool> EnqueueAsync(IEnumerable<Job> jobs, CancellationToken ct)
    {
        var allQueued = true;
        foreach (var job in jobs)
        {
            if (!queue.TryEnqueue(job.Id))
            {
                await reporter.FailAsync(job, "작업 큐가 가득 찼습니다", ct);
                allQueued = false;
            }
        }
        return allQueued;
    }

    public int QueueRoom(int capacity) => capacity - queue.Count;
}
