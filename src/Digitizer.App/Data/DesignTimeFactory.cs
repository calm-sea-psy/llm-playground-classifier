using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Digitizer.App.Data;

/// <summary>dotnet ef migrations add 용 (실행 중인 앱 · 사용자 DB 는 건드리지 않음)</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<DigitizerDb>
{
    public DigitizerDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DigitizerDb>().UseSqlite("Data Source=design.db").Options);
}
