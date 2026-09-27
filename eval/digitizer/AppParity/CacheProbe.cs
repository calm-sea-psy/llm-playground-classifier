using System.Text.Json.Nodes;
using Digitizer.Engine;

/// <summary>
/// 6-1 에서 영수증 필드가 평가 도구와 16/30 건 달랐던 원인 확인: Ollama 가 앞 요청과 겹치는 앞부분(지시문)의 계산(KV 캐시)을
/// 재사용하면 GPU 계산 순서가 달라져 비슷한 후보 사이에서 다른 토큰이 나올 수 있음. 평가 도구는 분류(다른 지시문) 뒤에 추출,
/// 설치판은 추출만 연달아 보냄 ➔ 같은 문장을 두 조건에서 보내 기준과 비교
/// </summary>
static class CacheProbe
{
    public static async Task RunAsync(string repo, string[] images, int rounds, string model, string korie)
    {
        var pack = DocumentType.Load(Path.Combine(repo, "packs", "receipt"));
        using var ollama = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434/"), Timeout = TimeSpan.FromMinutes(5) };
        var extractor = new FieldExtractor(ollama, new LlmOptions(model, MaxOutputTokens: 4096));
        // 처리와 같은 길 (추출 + 필수 필드 확인 + 검증 보정): 기준의 텍스트 시도 필드도 보정 뒤 값
        var processor = new DocumentProcessor(_ => extractor, PackPromptSource.Instance);
        var raw = Path.Combine(repo, "eval", "results", korie, "raw", model.Replace(':', '_'));
        JsonNode Result(string id) => JsonNode.Parse(File.ReadAllText(Path.Combine(raw, $"{id}.json")))!["result"]!;
        var other = Result("IMG00007")["readingText"]!.GetValue<string>();  // (나) 에서 먼저 보낼 다른 영수증

        var same = new Dictionary<string, int> { ["가"] = 0, ["나"] = 0 };
        var total = 0;
        foreach (var id in images)
        {
            var r = Result(id);
            var text = r["readingText"]!.GetValue<string>();
            // 텍스트 추출 시도 (최종이 VLM 폴백이어도 첫 추출끼리 비교)
            var expected = r["attempts"]!.AsArray().First(a => a!["source"]!.GetValue<string>() == "text")!["fields"]!.ToJsonString();
            for (var round = 1; round <= rounds; round++)
            {
                foreach (var condition in new[] { "가", "나" })
                {
                    if (condition == "가")
                        await extractor.ExtractMessagesAsync(pack, "다음 문서의 종류를 한 단어로 답하세요.", text[..Math.Min(1500, text.Length)], null);
                    else
                        await extractor.ExtractAsync(pack, other, "ocr");
                    var got = await processor.ExtractAsync(pack, model, new DocumentText(text, false, FieldExtractor.InputLabel("ocr")), null, null);
                    var equal = JsonNode.DeepEquals(JsonNode.Parse(expected), got.Fields);
                    if (equal) same[condition]++;
                    total++;
                    Console.WriteLine($"{id} r{round} ({condition}) 기준과 {(equal ? "같음" : "다름")}");
                }
            }
        }
        Console.WriteLine($"기준과 같음: (가) 다른 지시문 뒤 {same["가"]}/{total / 2} · (나) 같은 지시문 뒤 {same["나"]}/{total / 2}");
    }
}
