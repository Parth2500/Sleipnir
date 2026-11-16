using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace Sleipnir.Server.Bifrost;

/// <summary>
/// What Sleipnir asks Bifröst, over the private socket Bifröst names in <c>BIFROST_API_SOCKET</c>
/// (Bifröst design §8.6). Plain HTTP and JSON: Sleipnir shares no code with Bifröst.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class BifrostApiClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public BifrostApiClient(string socketPath)
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        }) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromSeconds(90) };
    }

    /// <summary>Asks the admin an approve-or-reject question and returns its id.</summary>
    public async Task<Guid> AskAsync(string title, string text, DateTime expiresAt, CancellationToken ct = default)
    {
        var body = new
        {
            title,
            text,
            choices = new[] { new { id = "approve", label = "Approve" }, new { id = "reject", label = "Reject" } },
            expiresAt,
            online = true,
        };
        using var response = await _http.PostAsJsonAsync("/v1/questions", body, Json, ct);
        response.EnsureSuccessStatusCode();
        var question = await response.Content.ReadFromJsonAsync<QuestionReply>(Json, ct);
        return question?.Id ?? throw new HttpRequestException("Bifröst sent no question id.");
    }

    /// <summary>Waits until the question is no longer open. A question Bifröst no longer knows counts as <c>gone</c>.</summary>
    public async Task<(string State, string? Choice)> WaitAsync(Guid id, CancellationToken ct = default)
    {
        while (true)
        {
            using var response = await _http.GetAsync($"/v1/questions/{id}?wait=30", ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return ("gone", null);
            response.EnsureSuccessStatusCode();
            var question = await response.Content.ReadFromJsonAsync<QuestionReply>(Json, ct);
            if (question is { State: not "open" and not null }) return (question.State, question.Choice);
        }
    }

    public async Task WithdrawAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"/v1/questions/{id}", ct);
        if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
    }

    /// <summary>A person's display name, or null when Bifröst does not know them.</summary>
    public async Task<string?> PersonNameAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"/v1/people/{id}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PersonReply>(Json, ct))?.DisplayName;
    }

    public void Dispose() => _http.Dispose();

    private sealed record QuestionReply(Guid Id, string? State, string? Choice);

    private sealed record PersonReply(string? DisplayName);
}
