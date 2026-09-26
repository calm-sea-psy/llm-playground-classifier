using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;

namespace AgentSpike;

/// <summary>0단계: OllamaSharp IChatClient 가 num_ctx · think · keep_alive 와 thinking 필드를 처리하는지, 도구 자동 루프가 도는지</summary>
public static class Probe
{
    public static async Task RunAsync(string[] args)
    {
        string[] models = args.Length > 0 ? args : ["gemma4:12b", "qwen3-vl:8b", "qwen3-vl:8b-instruct"];

        var http = new HttpClient(new LoggingHandler(new HttpClientHandler()))
        {
            BaseAddress = new Uri("http://127.0.0.1:11434"),
            Timeout = TimeSpan.FromMinutes(5),
        };

        [Description("실험 하나의 조합별 점수를 조회")]
        static string GetExperiment([Description("실험 id")] string id) =>
            id == "7f3a"
                ? """{"id":"7f3a","combos":[{"index":0,"model":"gemma4:12b","score":0.912},{"index":1,"model":"qwen3-vl:8b","score":0.847}]}"""
                : """{"error":"실험을 찾을 수 없습니다"}""";

        var tool = AIFunctionFactory.Create(GetExperiment, "get_experiment");

        foreach (var model in models)
        {
            Console.WriteLine($"\n===== {model}");
            IChatClient ollama = new OllamaApiClient(http, model);

            var options = new ChatOptions { Temperature = 0 };
            options.AddOllamaOption(OllamaOption.NumCtx, 16384);
            // think · keep_alive 는 모델 options 가 아니라 요청 최상위 필드 ➔ 넘기는 방법 확인
            options.AdditionalProperties ??= [];
            options.AdditionalProperties["think"] = false;
            options.AdditionalProperties["keep_alive"] = "10m";

            // (1) 도구 없이 일반 답: content 가 비고 thinking 에만 답이 오는지
            var plain = await ollama.GetResponseAsync("영수증 합계 필드 이름 세 가지를 쉼표로만 답해", options);
            Console.WriteLine($"[plain] text=\"{plain.Text}\"");
            foreach (var c in plain.Messages.SelectMany(m => m.Contents))
            {
                Console.WriteLine($"  content {c.GetType().Name}: {Trim(c is TextReasoningContent r ? r.Text : c.ToString())}");
            }

            // (2) 도구 자동 루프
            var client = ollama.AsBuilder().UseFunctionInvocation().Build();
            var toolOptions = options.Clone();
            toolOptions.Tools = [tool];
            var answer = await client.GetResponseAsync("실험 7f3a 의 1위 조합과 점수를 알려줘", toolOptions);
            foreach (var m in answer.Messages)
            {
                foreach (var c in m.Contents)
                {
                    var text = c switch
                    {
                        FunctionCallContent f => $"call {f.Name}({string.Join(",", f.Arguments?.Select(a => $"{a.Key}={a.Value}") ?? [])}) id={f.CallId}",
                        FunctionResultContent r => $"result id={r.CallId}: {Trim(r.Result?.ToString())}",
                        TextContent t => $"text: {Trim(t.Text)}",
                        TextReasoningContent t => $"reasoning: {Trim(t.Text)}",
                        _ => c.GetType().Name,
                    };
                    Console.WriteLine($"  [{m.Role}] {text}");
                }
            }
        }

    }

    static string Trim(string? s) => s is null ? "" : (s.Length > 120 ? s[..120] + "…" : s).ReplaceLineEndings(" ");
    
    /// <summary>Ollama 로 실제 나가는 요청 본문의 최상위 필드와 options 를 출력</summary>
    public sealed class LoggingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                var fields = string.Join(" ", root.EnumerateObject()
                    .Where(p => p.Name is not ("messages" or "tools"))
                    .Select(p => $"{p.Name}={p.Value.GetRawText()}"));
                Console.WriteLine($"  >> {request.RequestUri?.AbsolutePath} {fields}");
            }
            return await base.SendAsync(request, ct);
        }
    }
}
