using Microsoft.Extensions.Options;

namespace Api.Shared.Storage;

public sealed class StorageOptions
{
    /// <summary>ContentRoot(src/Api) 기준 상대 경로 또는 절대 경로</summary>
    public string UploadRoot { get; set; } = "../../data/uploads";
    public int MaxUploadMb { get; set; } = 20;
}

/// <summary>data/uploads/{jobId}/ 에 원본과 단계별 결과 JSON 을 함께 저장한다.</summary>
public sealed class UploadStorage(IOptions<StorageOptions> options, IHostEnvironment env)
{
    public string Root { get; } = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.UploadRoot));

    public string PathOf(Guid jobId, string fileName) => Path.Combine(Root, jobId.ToString(), fileName);

    public async Task<string> SaveOriginalAsync(Guid jobId, IFormFile file, CancellationToken ct)
    {
        var storedName = "original" + Path.GetExtension(file.FileName).ToLowerInvariant();
        var path = PathOf(jobId, storedName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await file.CopyToAsync(stream, ct);
        return storedName;
    }

    public Stream OpenRead(Guid jobId, string fileName) => File.OpenRead(PathOf(jobId, fileName));

    public Task WriteBytesAsync(Guid jobId, string fileName, byte[] content, CancellationToken ct) =>
        File.WriteAllBytesAsync(PathOf(jobId, fileName), content, ct);

    public Task WriteTextAsync(Guid jobId, string fileName, string content, CancellationToken ct) =>
        File.WriteAllTextAsync(PathOf(jobId, fileName), content, ct);
}
