namespace Digitizer.App.Api;

/// <summary>
/// 이 PC 의 화면에서 온 요청만 통과. Kestrel 은 127.0.0.1 에만 열려 있지만 브라우저를 거친 공격은 막지 못함:
/// - DNS 리바인딩 (다른 사이트 이름이 127.0.0.1 을 가리키게 해서 원문 · 필드를 읽음) ➔ Host 가 localhost · 127.0.0.1 이 아니면 거부
/// - 다른 사이트가 폼으로 몰래 보내는 쓰기 요청 (업로드 · 승인) ➔ 쓰기 요청은 X-Digitizer 헤더 필수.
///   사용자 지정 헤더는 교차 출처에서 사전 확인(preflight)이 필요한데 CORS 를 열지 않았으므로 다른 사이트는 붙일 수 없음
/// </summary>
public sealed class LocalOnly(RequestDelegate next)
{
    public const string Header = "X-Digitizer";

    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (!(host is "localhost" or "127.0.0.1" or "[::1]" or "::1"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("이 PC 에서만 열 수 있습니다 (http://127.0.0.1)");
            return;
        }
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/api") && !context.Request.Headers.ContainsKey(Header))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = $"{Header} 헤더가 없는 요청은 받지 않습니다 (프로그램 화면에서만)" });
            return;
        }
        await next(context);
    }
}
