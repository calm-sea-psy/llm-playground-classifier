using Api.Shared.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Api.Shared.Data;

/// <summary>
/// 공통 DbContext. 모듈 테이블(ocr_results 등)은 각 모듈 폴더의 IEntityTypeConfiguration 으로 등록되어
/// Shared 가 모듈 타입을 직접 참조하지 않는다.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
