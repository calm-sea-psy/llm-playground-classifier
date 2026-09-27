using Digitizer.App.Data;
using Digitizer.App.Export;
using Digitizer.App.Processing;
using Digitizer.App.Review;
using Microsoft.EntityFrameworkCore;

namespace Digitizer.App;

public static class ServiceRegistration
{
    /// <summary>처리 코어 (앱 · 테스트 공통). 원문 추출(ISourceReader)과 추출기 만들기(Func&lt;AppSettings, string, IFieldExtractor&gt;)는 부르는 쪽이 등록</summary>
    public static IServiceCollection AddDigitizerCore(this IServiceCollection services, AppPaths paths, SettingsFile settings, string? packsRoot = null)
    {
        services.AddSingleton(paths);
        services.AddSingleton(settings);
        services.AddSingleton(new PackCatalog(packsRoot ?? PackCatalog.DefaultRoot));
        services.AddSingleton(TimeProvider.System);
        services.AddDbContextFactory<DigitizerDb>(o => o.UseSqlite($"Data Source={paths.Database}"));
        services.AddSingleton<FileRouter>();
        services.AddSingleton<DocumentRunner>();
        services.AddSingleton<ProcessingQueue>();
        services.AddSingleton<DocumentIntake>();
        services.AddSingleton<ReviewService>();
        services.AddSingleton<ExcelExporter>();
        return services;
    }
}
