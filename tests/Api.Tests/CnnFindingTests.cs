using Api.Modules.Image.Cnn;

namespace Api.Tests;

/// <summary>CNN 소견의 경계(판단 보류): 기준값 바로 위 0.02 이내 양성</summary>
public class CnnFindingTests
{
    [Theory]
    [InlineData(0.5003, 0.5, true, true)]    // 심장비대 0.50 = 기준값에 걸침 (4차 리뷰 사례)
    [InlineData(0.519, 0.5, true, true)]
    [InlineData(0.52, 0.5, true, false)]     // 기준값 + 0.02 부터는 확정 양성
    [InlineData(0.49, 0.5, false, false)]    // 음성은 경계가 아님
    [InlineData(0.525, 0.515, true, true)]   // 소견별 기준값(결절 0.515)에도 같은 폭
    [InlineData(0.990, 0.989, true, false)]  // 소아 폐렴 모델(기준 0.989)은 적용 안 함
    public void 기준값_바로_위만_경계(double probability, double threshold, bool positive, bool borderline)
    {
        Assert.Equal(borderline, new CnnFinding("X", probability, threshold, positive).Borderline);
    }

    [Fact]
    public void 확정_양성은_경계를_뺀다()
    {
        var result = new CnnResult("xrv", "v", 1, 1,
            [new("Cardiomegaly", 0.505, 0.5, true), new("Effusion", 0.71, 0.5, true), new("Edema", 0.3, 0.5, false)], null, 1);
        Assert.Equal(["Effusion"], result.DefinitePositives.Select(f => f.Label));
        Assert.Equal(2, result.Positives.Count());
    }
}
