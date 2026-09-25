using Microsoft.AspNetCore.Diagnostics;

namespace Api.Shared.Http;

/// <summary>
/// Minimal API 바인딩 실패(파일 없이 업로드 등)는 Development 에서 BadHttpRequestException 으로 던져지는데,
/// UseExceptionHandler 가 이를 500 으로 바꿔 버리므로 원래 상태 코드(400 등)의 ProblemDetails 로 돌려준다.
/// </summary>
public sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }
        context.Response.StatusCode = badRequest.StatusCode;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails =
            {
                Status = badRequest.StatusCode,
                Title = "잘못된 요청",
                Detail = badRequest.Message,
            },
        });
    }
}
