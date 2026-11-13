using System.Text.Json.Serialization;

namespace Sleipnir.Server.Api;

/// <summary>RFC 9457 problem details with a stable machine-readable <c>code</c>.</summary>
public sealed record ProblemBody(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("code")] string Code);

public static class Problem
{
    public static IResult Create(int status, string code, string title) =>
        Results.Json(new ProblemBody("about:blank", title, status, code), contentType: "application/problem+json", statusCode: status);

    public static Task WriteAsync(HttpContext context, int status, string code, string title) =>
        Create(status, code, title).ExecuteAsync(context);
}
