using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

// 4차-exe 6-1 · 6-3: 설치판(exe)에 문서를 올려 대기열로 처리 ➔ 결과 DB 를 직접 읽어 평가 도구 측정 결과와 대조
// (화면 API 는 원문의 주민등록번호를 가려서 원문 비교가 안 됨). 실행 중인 앱의 주소와 그 앱의 데이터 폴더를 넘김
//   run --app http://127.0.0.1:5320 --data <DIGITIZER_DATA> --out <이름> [--sets resume,receipt]
//       [--resume-texts holdout-v3] [--resume-extract holdout-v3] [--model gemma4:12b] [--cpu] [--inputs pdf,docx,scan]
//       [--korie E_korie_processor] [--receipts 30]
// 결과: eval/results/digitizer/<이름>/parity.jsonl · summary.md (git 제외)
Console.OutputEncoding = Encoding.UTF8;
var repo = FindRepo();
string Opt(string name, string fallback) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? fallback;
var json = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

// cache-probe --images IMG00033,IMG00465 [--rounds 2]: 같은 추출 문장이 "바로 앞 요청" 에 따라 결과가 달라지는지
//   (가) 지시문이 다른 요청(평가 도구의 분류 단계 흉내) 뒤  (나) 다른 영수증 추출(같은 지시문) 뒤 ➔ 기준(평가 도구)과 같은지
if (args.FirstOrDefault() == "cache-probe")
{
    await CacheProbe.RunAsync(repo, Opt("--images", "IMG00033").Split(','), int.Parse(Opt("--rounds", "2")), Opt("--model", "gemma4:12b"), Opt("--korie", "E_korie_processor"));
    return;
}

var appUrl = Opt("--app", "http://127.0.0.1:5320").TrimEnd('/');
var dataDir = Opt("--data", "") is { Length: > 0 } d ? d : throw new ArgumentException("--data (앱의 DIGITIZER_DATA) 가 필요합니다");
var outDir = Directory.CreateDirectory(Path.Combine(repo, "eval", "results", "digitizer", Opt("--out", "app-parity"))).FullName;
var sets = Opt("--sets", "resume,receipt").Split(',');
var model = Opt("--model", "gemma4:12b");
var cpu = args.Contains("--cpu");
var inputs = Opt("--inputs", "pdf,docx,scan").Split(',');
var staging = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"app-parity-{Guid.NewGuid():N}")).FullName;

// 1) 올릴 파일 (이름 = 문서id__입력.확장자 ➔ 결과를 기준과 짝지음)
var cases = new List<Case>();
if (sets.Contains("resume"))
{
    var textsRun = Path.Combine(repo, "eval", "results", "digitizer", Opt("--resume-texts", "holdout-v3"));
    var extractRun = Path.Combine(repo, "eval", "results", "digitizer", Opt("--resume-extract", Opt("--resume-texts", "holdout-v3")));
    var texts = ReadJsonl(Path.Combine(textsRun, "texts.jsonl")).ToDictionary(t => $"{t["id"]}|{t["input"]}");
    var extracts = ReadJsonl(Path.Combine(extractRun, "extract.jsonl"))
        .Where(e => e["model"]!.GetValue<string>() == model && e["cpu"]!.GetValue<bool>() == cpu && e["rep"]!.GetValue<int>() == 1)
        .ToDictionary(e => $"{e["id"]}|{e["input"]}");
    foreach (var dir in Directory.GetDirectories(Path.Combine(repo, "data", "digitizer", "resume", "holdout")).Order())
    {
        var id = Path.GetFileName(dir);
        foreach (var input in inputs)
        {
            // --time-only: 추출 기준이 없는 입력도 넣음 (원문 · 처리 시간만 비교, 예: CPU 측정은 스캔만 있음)
            if (!texts.TryGetValue($"{id}|{input}", out var t)) continue;
            if (!extracts.TryGetValue($"{id}|{input}", out var e) && !args.Contains("--time-only")) continue;
            string file;
            var note = (string?)null;
            if (input != "scan")
            {
                file = Path.Combine(staging, $"{id}__{input}.{input}");
                File.Copy(Path.Combine(dir, $"doc.{input}"), file);
            }
            else
            {
                var pages = Directory.GetFiles(dir, "scan_*.png").Order().ToList();
                if (pages.Count == 1)
                {
                    file = Path.Combine(staging, $"{id}__scan.png");
                    File.Copy(pages[0], file);
                }
                else
                {
                    // 여러 쪽 스캔: exe 는 파일 1개 = 문서 1건 ➔ 쪽 이미지를 그대로 담은 PDF 로 (측정은 쪽 PNG 를 이어 OCR)
                    file = Path.Combine(staging, $"{id}__scan.pdf");
                    File.WriteAllBytes(file, ImagePdf(pages));
                    note = $"{pages.Count}쪽 스캔 ➔ 이미지 PDF";
                }
            }
            var expectedErrors = e is null ? -1 : e["issues"]!.AsArray().Count(i => i!["severity"]!.GetValue<string>() == "Error") + (e["error"] is null ? 0 : 1);
            cases.Add(new Case("resume", id, input, file, t["text"]!.GetValue<string>(), e?["fields"], expectedErrors, null, null,
                e is null ? (note is null ? "추출 기준 없음 (원문 · 시간만)" : note + " · 추출 기준 없음") : note));
        }
    }
}
if (sets.Contains("receipt"))
{
    var korie = Path.Combine(repo, "eval", "results", Opt("--korie", "E_korie_processor"), "raw", model.Replace(':', '_'));
    var take = int.Parse(Opt("--receipts", "30"));
    var all = Directory.GetFiles(korie, "*.json").Order().ToList();
    foreach (var raw in all.Where((_, i) => i % Math.Max(1, all.Count / take) == 0).Take(take))
    {
        var r = JsonNode.Parse(File.ReadAllText(raw))!["result"]!;
        var id = Path.GetFileNameWithoutExtension(raw);
        // KORIE 이미지는 png · jpeg 가 섞여 있음
        var image = Directory.GetFiles(Path.Combine(repo, "data", "samples", "korie", "images"), $"{id}.*").Single();
        var file = Path.Combine(staging, $"{id}__image{Path.GetExtension(image)}");
        File.Copy(image, file);
        cases.Add(new Case("receipt", id, "image", file, r["readingText"]!.GetValue<string>(), r["fields"], r["errorCount"]!.GetValue<int>(),
            r["finalSource"]?.GetValue<string>(), r["fallbackUsed"]!.GetValue<bool>(), null));
    }
}
Console.WriteLine($"대조 {cases.Count}건 (이력서 {cases.Count(c => c.Pack == "resume")} · 영수증 {cases.Count(c => c.Pack == "receipt")}) · 모델 {model}{(cpu ? " (CPU)" : "")}");

// 2) 올리기 (화면 업로드와 같은 대기열)
using var http = new HttpClient { BaseAddress = new Uri(appUrl + "/"), Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.Add("X-Digitizer", "1");
var docIds = new Dictionary<string, long>();
var sw = Stopwatch.StartNew();
foreach (var group in cases.GroupBy(c => c.Pack))
foreach (var chunk in group.Chunk(10))
{
    using var form = new MultipartFormDataContent { { new StringContent(group.Key), "pack" } };
    foreach (var c in chunk)
    {
        var content = new ByteArrayContent(File.ReadAllBytes(c.File));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "files", Path.GetFileName(c.File));
    }
    using var response = await http.PostAsync("api/upload", form);
    var results = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
    foreach (var r in results)
    {
        if (r!["id"] is null) throw new InvalidOperationException($"올리기 실패: {r}");
        if (r["status"]!.GetValue<string>() == "Duplicate") throw new InvalidOperationException($"이미 접수한 내용입니다 (새 데이터 폴더로): {r["name"]}");
        docIds[r["name"]!.GetValue<string>()] = r["id"]!.GetValue<long>();
    }
}

// 3) 처리 끝날 때까지
var pending = new HashSet<long>(docIds.Values);
var last = -1;
while (pending.Count > 0)
{
    await Task.Delay(5000);
    var docs = JsonNode.Parse(await http.GetStringAsync("api/documents?take=2000"))!.AsArray();
    foreach (var doc in docs)
        if (doc!["status"]!.GetValue<string>() is not ("Queued" or "Processing")) pending.Remove(doc["id"]!.GetValue<long>());
    if (pending.Count != last) Console.WriteLine($"  남은 {pending.Count}건 · {sw.Elapsed:mm\\:ss}");
    last = pending.Count;
}

// 4) 결과 DB 에서 문서마다 마지막 추출 (원문은 가리지 않은 그대로)
var rows = new Dictionary<long, JsonObject>();
await using (var db = new SqliteConnection($"Data Source={Path.Combine(dataDir, "digitizer.db")};Mode=ReadOnly"))
{
    await db.OpenAsync();
    var cmd = db.CreateCommand();
    cmd.CommandText = """
        SELECT d.Id, d.Status, d.StatusReason, x.SourceText, x.Fields, x.Issues, x.FinalSource, x.FallbackUsed, x.ErrorCount, x.TextMs, x.LlmMs, x.Model, x.SourceKind
        FROM documents d LEFT JOIN extractions x ON x.Id = (SELECT MAX(Id) FROM extractions WHERE DocumentId = d.Id)
        """;
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        string? S(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        rows[reader.GetInt64(0)] = new JsonObject
        {
            ["status"] = S(1), ["reason"] = S(2), ["text"] = S(3),
            ["fields"] = S(4) is { } f ? JsonNode.Parse(f) : null,
            ["errors"] = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            ["final"] = S(6), ["fallback"] = !reader.IsDBNull(7) && reader.GetBoolean(7),
            ["ms"] = reader.IsDBNull(9) ? null : reader.GetInt64(9) + reader.GetInt64(10),
            ["model"] = S(11), ["source"] = S(12),
        };
    }
}

// 5) 대조
var lines = new List<JsonObject>();
foreach (var c in cases)
{
    var got = rows[docIds[Path.GetFileName(c.File)]];
    var fieldDiffs = Diff(c.Fields, got["fields"]);
    var line = new JsonObject
    {
        ["pack"] = c.Pack, ["id"] = c.Id, ["input"] = c.Input, ["note"] = c.Note,
        ["status"] = got["status"]?.DeepClone(), ["model"] = got["model"]?.DeepClone(), ["ms"] = got["ms"]?.DeepClone(),
        ["same_text"] = got["text"]?.GetValue<string>() == c.Text,
        ["same_fields"] = fieldDiffs.Count == 0,
        ["field_diffs"] = new JsonArray([.. fieldDiffs.Select(x => (JsonNode)JsonValue.Create(x)!)]),
        ["errors"] = got["errors"]?.DeepClone(), ["expected_errors"] = c.Errors,
        ["same_errors"] = got["errors"]?.GetValue<int>() == c.Errors,
        ["final"] = got["final"]?.DeepClone(), ["expected_final"] = c.FinalSource,
        ["fallback"] = got["fallback"]?.DeepClone(), ["expected_fallback"] = c.Fallback,
    };
    if (c.FinalSource is not null) line["same_final"] = got["final"]?.GetValue<string>() == c.FinalSource && got["fallback"]!.GetValue<bool>() == c.Fallback;
    lines.Add(line);
}
await File.WriteAllLinesAsync(Path.Combine(outDir, "parity.jsonl"), lines.Select(l => l.ToJsonString(json)));

// 6) 요약
var md = new StringBuilder();
md.AppendLine($"# 설치판 결과 대조 ({Path.GetFileName(outDir)})").AppendLine();
md.AppendLine($"- 모델 {model}{(cpu ? " · CPU 전용" : "")} · 앱 {appUrl} · 전체 {sw.Elapsed:hh\\:mm\\:ss}").AppendLine();
md.AppendLine("| 종류 | 입력 | 건수 | 원문 같음 | 필드 같음 | 검증 오류 수 같음 | 최종 출처 · 폴백 같음 | 처리 시간 중앙 | 최대 | 120초 넘음 |");
md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
foreach (var g in lines.GroupBy(l => (Pack: l["pack"]!.GetValue<string>(), Input: l["input"]!.GetValue<string>())))
{
    var ms = g.Select(l => l["ms"]?.GetValue<long>() ?? 0).Order().ToList();
    string Count(string key) => $"{g.Count(l => l[key]?.GetValue<bool>() == true)}/{g.Count()}";
    md.AppendLine($"| {g.Key.Pack} | {g.Key.Input} | {g.Count()} | {Count("same_text")} | {Count("same_fields")} | {Count("same_errors")} | " +
        $"{(g.Key.Pack == "receipt" ? Count("same_final") : "-")} | {ms[ms.Count / 2] / 1000.0:0.0}초 | {ms[^1] / 1000.0:0.0}초 | {ms.Count(m => m > 120_000)} |");
}
md.AppendLine().AppendLine("## 다른 건").AppendLine();
foreach (var l in lines.Where(l => !(l["same_text"]!.GetValue<bool>() && l["same_fields"]!.GetValue<bool>() && l["same_errors"]!.GetValue<bool>()
             && (l["same_final"]?.GetValue<bool>() ?? true))))
{
    md.AppendLine($"- {l["pack"]} {l["id"]} {l["input"]}{(l["note"] is null ? "" : $" ({l["note"]})")}: " +
        $"원문 {(l["same_text"]!.GetValue<bool>() ? "같음" : "다름")} · 오류 {l["errors"]}/{l["expected_errors"]}" +
        (l["expected_final"] is null ? "" : $" · 최종 {l["final"]}/{l["expected_final"]} 폴백 {l["fallback"]}/{l["expected_fallback"]}") +
        $" · 필드 {string.Join(", ", l["field_diffs"]!.AsArray().Select(x => x!.GetValue<string>()).Take(8))}");
}
await File.WriteAllTextAsync(Path.Combine(outDir, "summary.md"), md.ToString());
Console.WriteLine(md);
Directory.Delete(staging, recursive: true);

static List<string> Diff(JsonNode? expected, JsonNode? actual)
{
    var a = Flatten(expected, "");
    var b = Flatten(actual, "");
    return a.Keys.Union(b.Keys).Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k)).Order().ToList();
}

// 칸 경로 ➔ 값 (숫자는 1 과 1.0 을 같게)
static Dictionary<string, string?> Flatten(JsonNode? node, string path)
{
    var result = new Dictionary<string, string?>();
    switch (node)
    {
        case JsonObject o:
            foreach (var (k, v) in o) foreach (var kv in Flatten(v, path.Length == 0 ? k : $"{path}.{k}")) result[kv.Key] = kv.Value;
            break;
        case JsonArray arr:
            for (var i = 0; i < arr.Count; i++) foreach (var kv in Flatten(arr[i], $"{path}[{i}]")) result[kv.Key] = kv.Value;
            if (arr.Count == 0) result[path] = "[]";
            break;
        case JsonValue v when v.TryGetValue<double>(out var dbl):
            result[path] = dbl.ToString(System.Globalization.CultureInfo.InvariantCulture);
            break;
        default:
            result[path] = node?.ToJsonString();
            break;
    }
    return result;
}

static byte[] ImagePdf(List<string> pngs)
{
    var builder = new PdfDocumentBuilder();
    foreach (var png in pngs)
    {
        var page = builder.AddPage(595, 842);
        page.AddPng(File.ReadAllBytes(png), new PdfRectangle(0, 0, 595, 842));
    }
    return builder.Build();
}

static IEnumerable<JsonNode> ReadJsonl(string path) => File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!);

static string FindRepo()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !File.Exists(Path.Combine(dir, "llm-playground-classifier.slnx"))) dir = Path.GetDirectoryName(dir);
    return dir ?? throw new DirectoryNotFoundException("저장소 루트를 찾지 못했습니다");
}

sealed record Case(string Pack, string Id, string Input, string File, string Text, JsonNode? Fields, int Errors, string? FinalSource, bool? Fallback, string? Note);
