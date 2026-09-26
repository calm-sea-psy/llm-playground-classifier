namespace AgentSpike;

/// <summary>2단계 정답 작성용: 과제에 쓸 값을 도구 코드로 직접 계산해 출력 (LLM 없음)</summary>
public static class Truth
{
    public static async Task RunAsync(ApiTools tools)
    {
        const string e1 = "01a0d2ae-1357-750d-8b25-f082ff40c9bb";
        const string daiso = "01a0d27c-0784-7180-9f34-357fdbf8f672";
        const string img667 = "01a0d2ae-141d-758d-bb00-0a232f71945f";
        Console.WriteLine(await tools.FindInOcr(daiso, "213-81-52063"));
        Console.WriteLine(await tools.FindInOcr(img667, "2025-10-31"));
        Console.WriteLine(await tools.FindInOcr(img667, "28840"));
        Console.WriteLine(await tools.ListExperimentDocs(e1, 0, "fallback"));
        Console.WriteLine(await tools.GetExtractionResult("01a0d2ae-1450-7b2c-b760-f1872ff51380"));
        Console.WriteLine(await tools.GetOcrText(daiso, 1));
        Console.WriteLine(await tools.GetOcrText("abc", 1));
    }
}
