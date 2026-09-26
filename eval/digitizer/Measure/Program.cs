using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Digitizer.Engine;
using Digitizer.Engine.Rules;

// 4차 0단계: src/Digitizer.Engine 으로 측정 (평가 도구 · exe 와 같은 엔진 ➔ 측정한 코드 = 배포할 코드)
//   text    --split dev --inputs pdf,docx,scan [--ocr-engine paddleocr] --out <run>    원문 추출만 (OCR 결과 저장 ➔ 모델마다 다시 안 돌림)
//   extract --texts <run> --models a,b [--cpu] [--reps 1] [--inputs pdf,docx,scan]    필드 추출 + 검증
// 결과: eval/results/digitizer/<run>/texts.jsonl · extract.jsonl (git 제외)
Console.OutputEncoding = Encoding.UTF8;

var repo = FindRepo();
var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
string Opt(string name, string fallback) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? fallback;
var type = DocumentType.Load(Path.Combine(repo, "packs", Opt("--type", "resume")));
var engineVersion = typeof(DocumentProcessor).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
Console.WriteLine($"문서 종류 {type.Id} v{type.Version} · Engine {engineVersion}");

var ollama = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434"), Timeout = TimeSpan.FromMinutes(10) };
var ocrHttp = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8001/"), Timeout = TimeSpan.FromMinutes(5) };

switch (args.FirstOrDefault())
{
    case "text":
    {
        var split = Opt("--split", "dev");
        var inputs = Opt("--inputs", "pdf,docx,scan").Split(',');
        var ocrEngine = args.Contains("--ocr-engine") ? Opt("--ocr-engine", "") : null;
        var outDir = Directory.CreateDirectory(Path.Combine(repo, "eval", "results", "digitizer", Opt("--out", $"{split}-text"))).FullName;
        var extractor = new TextExtractor(new OcrClient(ocrHttp, ocrEngine));
        var path = Path.Combine(outDir, "texts.jsonl");
        // 실패한 기록(예: OCR 서비스가 뜨기 전 연결 거부)은 지우고 다시 시도
        var kept = File.Exists(path) ? File.ReadAllLines(path).Where(l => JsonNode.Parse(l)!["error"] is null).ToList() : [];
        if (File.Exists(path)) await File.WriteAllLinesAsync(path, kept);
        var done = kept.Select(l => JsonNode.Parse(l)!).Select(n => $"{n["id"]}|{n["input"]}").ToHashSet();

        foreach (var dir in Directory.GetDirectories(Path.Combine(repo, "data", "digitizer", type.Id, split)).Order())
        {
            var id = Path.GetFileName(dir);
            foreach (var input in inputs)
            {
                if (done.Contains($"{id}|{input}")) continue;
                SourceText source;
                string? error = null;
                try
                {
                    source = input == "scan"
                        ? await extractor.ExtractPagesAsync(Directory.GetFiles(dir, "scan_*.png").Order())
                        : await extractor.ExtractAsync(Path.Combine(dir, $"doc.{input}"));
                }
                catch (Exception e)
                {
                    source = new SourceText("", input, 0, 0);
                    error = $"{e.GetType().Name}: {e.Message}";
                }
                var record = new JsonObject
                {
                    ["id"] = id, ["split"] = split, ["input"] = input, ["ocr_engine"] = input == "scan" ? ocrEngine ?? "default" : null,
                    ["source"] = source.Source, ["pages"] = source.Pages, ["ms"] = source.ElapsedMs, ["ocr_confidence"] = source.OcrConfidence,
                    ["text"] = source.Text, ["error"] = error,
                };
                await File.AppendAllTextAsync(path, record.ToJsonString(json) + "\n");
                Console.WriteLine($"{id} {input} · {source.Source} {source.Pages}쪽 · {source.ElapsedMs}ms · {source.Text.Length}자{(error is null ? "" : " · " + error)}");
            }
        }
        break;
    }

    case "extract":
    {
        var runDir = Path.Combine(repo, "eval", "results", "digitizer", Opt("--texts", "dev-text"));
        var models = Opt("--models", "gemma4:12b").Split(',');
        var reps = int.Parse(Opt("--reps", "1"));
        var cpu = args.Contains("--cpu");
        var inputs = Opt("--inputs", "pdf,docx,scan").Split(',');
        var only = args.Contains("--only") ? Opt("--only", "").Split(',').ToHashSet() : null;
        var texts = File.ReadAllLines(Path.Combine(runDir, "texts.jsonl")).Select(l => JsonNode.Parse(l)!)
            .Where(t => inputs.Contains(t["input"]!.GetValue<string>()) && t["error"] is null)
            .Where(t => only is null || only.Contains(t["id"]!.GetValue<string>()))
            .ToList();
        var path = Path.Combine(runDir, "extract.jsonl");
        // 키는 같은 방식으로 만들 것: JSON 의 false 를 문자열로 바꾸면 "false", C# bool 은 "False" ➔ 완료된 회차를 다시 돌렸음
        static string Key(string model, bool cpu, string id, string input, int rep) => $"{model}|{cpu}|{id}|{input}|{rep}";
        var done = File.Exists(path)
            ? File.ReadAllLines(path).Select(l => JsonNode.Parse(l)!)
                .Select(n => Key(n["model"]!.GetValue<string>(), n["cpu"]!.GetValue<bool>(), n["id"]!.GetValue<string>(), n["input"]!.GetValue<string>(), n["rep"]!.GetValue<int>()))
                .ToHashSet()
            : [];

        foreach (var model in models)
        {
            // 모델을 먼저 내림: CPU 측정(num_gpu=0)으로 올라간 인스턴스가 남아 있으면 GPU 요청도 그걸로 처리됨 (VRAM 0GB 로 43초)
            await ollama.PostAsync("api/generate", new StringContent(new JsonObject { ["model"] = model, ["keep_alive"] = 0 }.ToJsonString(), Encoding.UTF8, "application/json"));
            var processor = new DocumentProcessor(new TextExtractor(new OcrClient(ocrHttp)), new FieldExtractor(ollama, new LlmOptions(model, CpuOnly: cpu)));
            for (var rep = 1; rep <= reps; rep++)
            {
                foreach (var t in texts)
                {
                    var id = t["id"]!.GetValue<string>();
                    var input = t["input"]!.GetValue<string>();
                    if (!done.Add(Key(model, cpu, id, input, rep))) continue;
                    var source = new SourceText(t["text"]!.GetValue<string>(), t["source"]!.GetValue<string>(), t["pages"]!.GetValue<int>(), t["ms"]!.GetValue<long>());
                    ProcessResult r;
                    try
                    {
                        r = await processor.ProcessFromTextAsync(type, source);
                    }
                    catch (Exception e)
                    {
                        r = new ProcessResult(type.Id, type.Version, source, new Extraction(null, "", 0, 0, 0, 0, $"{e.GetType().Name}: {e.Message}"), []);
                    }
                    var record = new JsonObject
                    {
                        ["id"] = id, ["input"] = input, ["model"] = model, ["cpu"] = cpu, ["rep"] = rep,
                        ["type_version"] = type.Version, ["engine_version"] = engineVersion,
                        ["text_ms"] = source.ElapsedMs, ["llm_ms"] = r.Extraction.ElapsedMs,
                        ["input_tokens"] = r.Extraction.InputTokens, ["output_tokens"] = r.Extraction.OutputTokens,
                        ["attempts"] = r.Extraction.Attempts, ["error"] = r.Extraction.Error,
                        ["fields"] = r.Fields?.DeepClone(), ["raw"] = r.Fields is null ? r.Extraction.Raw : null,
                        ["needs_review"] = r.NeedsReview,
                        ["issues"] = new JsonArray([.. r.Issues.Select(i => (JsonNode)new JsonObject
                            { ["rule"] = i.Rule, ["path"] = i.Field, ["severity"] = i.Severity.ToString(), ["message"] = i.Message })]),
                    };
                    await File.AppendAllTextAsync(path, record.ToJsonString(json) + "\n");
                    Console.WriteLine($"{model}{(cpu ? " (CPU)" : "")} r{rep} {id} {input} · {r.Extraction.ElapsedMs / 1000.0:0.0}초 · 문제 {r.Issues.Count(i => i.Severity == IssueSeverity.Error)}" +
                        (r.Extraction.Error is null ? "" : " · " + r.Extraction.Error));
                }
            }
        }
        break;
    }

    default:
        Console.WriteLine("사용법: text --split dev --inputs pdf,docx,scan [--ocr-engine x] --out <run> | extract --texts <run> --models a,b [--cpu] [--reps n] [--inputs ..] [--only id,..]");
        break;
}

static string FindRepo()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !File.Exists(Path.Combine(dir, "llm-playground-classifier.slnx"))) dir = Path.GetDirectoryName(dir);
    return dir ?? throw new DirectoryNotFoundException("평가 저장소 루트를 찾지 못했습니다");
}
