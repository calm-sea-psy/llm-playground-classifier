using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Digitizer.Engine;

namespace Digitizer.App;

/// <summary>
/// 사용자 설정 (%LocalAppData%\Digitizer\settings.json). 처음 실행하면 기본값으로 만든다.
/// 처리 설정 기본값은 평가 도구에서 측정한 조합 그대로 (src/Api/appsettings.json: gemma4:12b · paddleocr · 큰 이미지만 LLM 내리기 ·
/// 폴백 신뢰도 0.9 · 폴백 이미지 1600px · num_ctx 16384 · 최대 출력 4096) ➔ 바꾸면 측정한 결과와 달라질 수 있음
/// </summary>
public sealed record AppSettings
{
    /// <summary>사용자가 보는 폴더 (넣기 · 처리됨 · 확인 필요 · 실패 · 내보내기). 비어 있으면 문서\문서 전산화</summary>
    public string DocumentsRoot { get; init; } = "";

    /// <summary>원본 · 원문 보관 기한 (일). 접수한 때로부터 이 기간이 되면 지움 (RetentionService). 0 = 지우지 않음 (개인정보가 계속 남음)</summary>
    public int RetentionDays { get; init; } = 90;

    /// <summary>
    /// 내보낸 엑셀 파일 보관 기한 (일). 내보낸 때로부터 이 기간이 되면 내보내기 폴더의 파일을 지움 (기록은 "지움" 으로 남김).
    /// 0 = 지우지 않음. 엑셀은 다른 곳에 넘기는 결과물이라 원본 보관 기한과 따로 정함
    /// </summary>
    public int ExportRetentionDays { get; init; } = 90;

    /// <summary>설치 도우미가 사양에 맞춰 정함 (GPU 8GB+ gemma4:12b, 없으면 gemma-4-E4B)</summary>
    public string Model { get; init; } = "gemma4:12b";

    /// <summary>
    /// 팩별로 합격 판정에 쓴 모델 (docs/pack_reports.md: 이력서 gemma4:12b · E4B, 영수증 KORIE 는 gemma4:12b 만).
    /// 다른 모델도 쓸 수 있지만 측정한 결과와 달라질 수 있음 ➔ 설정 화면이 경고
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> MeasuredModels = new Dictionary<string, string[]>
    {
        ["resume"] = ["gemma4:12b", "hf.co/unsloth/gemma-4-E4B-it-GGUF:Q4_K_M"],
        ["receipt"] = ["gemma4:12b"],
    };
    public bool CpuOnly { get; init; }
    public string OllamaUrl { get; init; } = "http://127.0.0.1:11434";
    public string OcrUrl { get; init; } = "http://127.0.0.1:8001";
    public string OcrEngine { get; init; } = "paddleocr";

    public bool VlmFallback { get; init; } = true;
    public double FallbackConfidence { get; init; } = 0.9;
    public int VlmMaxImageSide { get; init; } = 1600;
    public UnloadPolicy UnloadBeforeOcr { get; init; } = UnloadPolicy.LargeImages;
    public double UnloadAboveMegapixels { get; init; } = 5.0;
    public int ContextLength { get; init; } = 16384;
    public string KeepAlive { get; init; } = "10m";
    public int MaxOutputTokens { get; init; } = 4096;
    public int LlmTimeoutSeconds { get; init; } = 300;
    /// <summary>GPU 없는 PC 는 여러 쪽 스캔 OCR 이 오래 걸림 (평가 도구의 30초보다 넉넉히)</summary>
    public int OcrTimeoutSeconds { get; init; } = 180;

    /// <summary>
    /// OCR 서비스 폴더 (main.py 와 .venv 가 있는 곳). 비어 있으면 실행 폴더의 ocr\. OcrUrl 에 이미 서비스가 떠 있으면 그걸 쓰고,
    /// 없으면 프로그램이 이 폴더의 서비스를 자식 프로세스로 실행 (프로그램이 끝나면 같이 끝남)
    /// </summary>
    public string OcrServiceDir { get; init; } = "";
    /// <summary>시작할 때 Ollama 가 꺼져 있으면 실행 시도 (Ollama 는 프로그램이 끝나도 그대로 둠, 사용자 프로그램이므로)</summary>
    public bool StartOllama { get; init; } = true;

    /// <summary>감시 폴더: 크기 · 수정 시각이 이 시간 동안 그대로여야 처리 (복사 중인 파일 건너뜀)</summary>
    public double StableSeconds { get; init; } = 2;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    [JsonIgnore]  // 계산값: 설정 파일에 쓰지 않음
    public string ResolvedDocumentsRoot => DocumentsRoot.Length > 0
        ? DocumentsRoot
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "문서 전산화");

    /// <summary>잘못된 값이면 사유 목록 (시작할 때 · 설정 화면에서 저장 전)</summary>
    public List<string> Problems()
    {
        var problems = new List<string>();
        if (RetentionDays < 0) problems.Add("보관 기한은 0(지우지 않음) 이상이어야 합니다");
        if (ExportRetentionDays < 0) problems.Add("엑셀 보관 기한은 0(지우지 않음) 이상이어야 합니다");
        if (string.IsNullOrWhiteSpace(Model)) problems.Add("모델이 비어 있습니다");
        if (FallbackConfidence is < 0 or > 1) problems.Add("폴백 신뢰도 기준은 0~1 사이여야 합니다");
        if (!Uri.TryCreate(OllamaUrl, UriKind.Absolute, out _)) problems.Add($"Ollama 주소가 올바르지 않습니다: {OllamaUrl}");
        if (!Uri.TryCreate(OcrUrl, UriKind.Absolute, out _)) problems.Add($"OCR 주소가 올바르지 않습니다: {OcrUrl}");
        if (StableSeconds < 0) problems.Add("안정 대기 시간은 0 이상이어야 합니다");
        return problems;
    }
}

/// <summary>설정 파일 읽기 · 쓰기. 파일이 없으면 기본값으로 만들고, 새 항목이 생긴 버전에서는 빠진 항목만 기본값으로 채워 다시 씀</summary>
public sealed class SettingsFile(string path)
{
    private readonly Lock _lock = new();
    private AppSettings? _current;

    public string Path { get; } = path;

    public AppSettings Current
    {
        get
        {
            lock (_lock) return _current ??= LoadOrCreate();
        }
    }

    public void Save(AppSettings settings)
    {
        var problems = settings.Problems();
        if (problems.Count > 0) throw new ArgumentException(string.Join("; ", problems));
        lock (_lock)
        {
            Write(settings);
            _current = settings;
        }
    }

    private AppSettings LoadOrCreate()
    {
        if (!File.Exists(Path))
        {
            var defaults = new AppSettings();
            Write(defaults);
            return defaults;
        }
        var text = File.ReadAllText(Path);
        var settings = JsonSerializer.Deserialize<AppSettings>(text, AppSettings.Json)
            ?? throw new InvalidDataException($"설정 파일을 읽지 못했습니다: {Path}");
        var normalized = JsonSerializer.Serialize(settings, AppSettings.Json);
        if (normalized != text) Write(settings);  // 빠진 항목을 기본값으로 채워 둠 (사용자가 열어 볼 때 모든 항목이 보이게)
        return settings;
    }

    private void Write(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, AppSettings.Json));
        File.Move(temp, Path, overwrite: true);
    }
}
