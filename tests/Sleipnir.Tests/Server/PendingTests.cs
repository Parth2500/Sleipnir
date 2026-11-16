using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sleipnir.Core.Models;
using Sleipnir.Infrastructure.Persistence;
using Sleipnir.Infrastructure.Storage;
using Sleipnir.Server.Access;
using Sleipnir.Server.Bifrost;
using Sleipnir.Server.Hosting;
using Sleipnir.Services;
using Sleipnir.Tests.Fakes;
using static Sleipnir.Tests.Server.ExtensionFixture;

namespace Sleipnir.Tests.Server;

/// <summary>Bifröst's extension API, as much of it as Sleipnir uses, on a temporary socket.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class FakeBifrost : IAsyncDisposable
{
    public sealed class Asked
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Title { get; init; } = "";
        public string Text { get; init; } = "";
        public string State { get; set; } = "open";
        public string? Choice { get; set; }
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<Guid, Asked> _known = new();
    private WebApplication? _app;

    /// <summary>Every question ever asked, in order, including ones a "restart" forgot.</summary>
    public ConcurrentQueue<Asked> Questions { get; } = new();
    public IEnumerable<Asked> Open => _known.Values.Where(q => q.State == "open");
    public Dictionary<Guid, string> People { get; } = [];

    public static async Task<FakeBifrost> StartAsync(string socket)
    {
        var fake = new FakeBifrost();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRoutingCore();
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenUnixSocket(socket));
        var app = builder.Build();
        app.MapPost("/v1/questions", async (HttpContext context) =>
        {
            var body = (await JsonDocument.ParseAsync(context.Request.Body)).RootElement;
            var asked = new Asked { Title = body.GetProperty("title").GetString()!, Text = body.GetProperty("text").GetString()! };
            fake._known[asked.Id] = asked;
            fake.Questions.Enqueue(asked);
            return Results.Json(new { id = asked.Id, state = "open" }, statusCode: 201);
        });
        app.MapGet("/v1/questions/{id:guid}", async (Guid id) =>
        {
            if (!fake._known.TryGetValue(id, out var asked)) return Results.NotFound();
            await Task.WhenAny(asked.Closed.Task, Task.Delay(300)); // a short "long poll"
            return fake._known.ContainsKey(id) ? Results.Json(new { id, state = asked.State, choice = asked.Choice }) : Results.NotFound();
        });
        app.MapDelete("/v1/questions/{id:guid}", (Guid id) =>
        {
            if (!fake._known.TryGetValue(id, out var asked)) return Results.NotFound();
            fake.Close(asked, "withdrawn", null);
            return Results.NoContent();
        });
        app.MapGet("/v1/people/{id:guid}", (Guid id) =>
            fake.People.TryGetValue(id, out var name) ? Results.Json(new { id, displayName = name, role = "member" }) : Results.NotFound());
        await app.StartAsync();
        fake._app = app;
        return fake;
    }

    public void Answer(Asked asked, string choice) => Close(asked, "answered", choice);

    public void Expire(Asked asked) => Close(asked, "expired", null);

    /// <summary>What a restart of Bifröst does to its questions: they are simply gone.</summary>
    public void ForgetEverything()
    {
        foreach (var asked in _known.Values.ToList())
        {
            _known.TryRemove(asked.Id, out _);
            asked.Closed.TrySetResult();
        }
    }

    private void Close(Asked asked, string state, string? choice)
    {
        if (asked.State != "open") return;
        asked.Choice = choice;
        asked.State = state;
        asked.Closed.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}

/// <summary>Sleipnir as an extension with the real engine, a fake network and a fake Bifröst.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class PendingFixture : IAsyncDisposable
{
    public const string Url = "http://files.example.test/films/film.mkv";
    private static readonly DownloadOptions Fast = new() { SegmentCount = 1, MaxRetries = 1, RetryDelay = TimeSpan.Zero };

    public required string Folder { get; init; }
    public required FakeBifrost Bifrost { get; init; }
    public required FakeNetworkClient Network { get; init; }
    public DownloadService Engine { get; private set; } = null!;
    public ApprovalCoordinator Approvals { get; private set; } = null!;
    public DaemonHost Host { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    private BifrostApiClient _client = null!;
    private ApprovalRule _rule;

    public static async Task<PendingFixture> StartAsync(ApprovalRule rule = ApprovalRule.Members)
    {
        var folder = Path.Combine(Path.GetTempPath(), "sleipnir-pend-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var fixture = new PendingFixture
        {
            Folder = folder,
            Bifrost = await FakeBifrost.StartAsync(Path.Combine(folder, "bifrost.sock")),
            Network = new FakeNetworkClient(TestHelpers.RandomBytes(50_000)),
            _rule = rule,
        };
        fixture.Bifrost.People[Alice] = "Alice";
        fixture.Bifrost.People[Bob] = "Bob";
        await fixture.StartSleipnirAsync();
        return fixture;
    }

    /// <summary>Starts Sleipnir on the folder's saved state, as a (re)start does.</summary>
    public async Task StartSleipnirAsync()
    {
        Engine = new DownloadService(Network, new LocalFileStorage(), Fast, new JsonDownloadStateStore(Path.Combine(Folder, "state")));
        _client = new BifrostApiClient(Path.Combine(Folder, "bifrost.sock"));
        Approvals = new ApprovalCoordinator(Engine, _client, new ExtensionSettings(_rule), NullLogger.Instance);
        var socket = Path.Combine(Folder, "extension.sock");
        Host = DaemonHost.Create(new DaemonOptions(PerPersonRoot: Path.Combine(Folder, "downloads")), Engine, new BifrostDoor(socket), Approvals);
        await Host.StartAsync();
        await Engine.RestoreAsync();
        Approvals.Start();
        Http = ClientFor(socket);
    }

    public async Task StopSleipnirAsync()
    {
        Http.Dispose();
        await Host.DisposeAsync();
        await Approvals.DisposeAsync();
        await Engine.ShutdownAsync();
        _client.Dispose();
    }

    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, Guid person, string role = "member", string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Bifrost-Person-Id", person.ToString());
        request.Headers.Add("X-Bifrost-Role", role);
        request.Headers.Add("X-Bifrost-Device-Id", Device.ToString());
        request.Headers.Add("X-Bifrost-Device-Kind", "app");
        if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return await Http.SendAsync(request);
    }

    /// <summary>Adds one download and returns it as the API answered.</summary>
    public async Task<JsonElement> AddAsync(Guid person, string role = "member")
    {
        var response = await Send(HttpMethod.Post, "/api/v1/downloads", person, role, JsonSerializer.Serialize(new { urls = new[] { Url } }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("items")[0];
    }

    public async Task<FakeBifrost.Asked> TheOpenQuestionAsync()
    {
        Assert.True(await TestHelpers.WaitUntil(() => Bifrost.Open.Any()), "no question was asked");
        return Assert.Single(Bifrost.Open);
    }

    public async ValueTask DisposeAsync()
    {
        await StopSleipnirAsync();
        await Bifrost.DisposeAsync();
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
    }
}

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class PendingTests
{
    private static Guid Id(JsonElement download) => download.GetProperty("id").GetGuid();

    [Fact]
    public async Task A_members_download_waits_for_approval_and_starts_when_approved()
    {
        await using var home = await PendingFixture.StartAsync();

        var added = await home.AddAsync(Bob);

        Assert.Equal("pending", added.GetProperty("status").GetString());
        Assert.Equal("waiting", added.GetProperty("approval").GetString());
        var question = await home.TheOpenQuestionAsync();
        Assert.Equal("Download for Bob?", question.Title);
        await Task.Delay(100);
        Assert.Equal(0, home.Network.RequestCount); // nothing is fetched before the answer
        Assert.Equal(DownloadStatus.Pending, home.Engine.GetDownload(Id(added))!.Status);

        home.Bifrost.Answer(question, "approve");

        Assert.True(await TestHelpers.WaitUntil(() => home.Engine.GetDownload(Id(added))!.Status == DownloadStatus.Completed));
        var task = home.Engine.GetDownload(Id(added))!;
        Assert.Null(task.Approval);
        Assert.True(File.Exists(task.DestinationPath));
    }

    [Fact]
    public async Task Rejected_is_cancelled_and_says_rejected()
    {
        await using var home = await PendingFixture.StartAsync();
        var added = await home.AddAsync(Bob);

        home.Bifrost.Answer(await home.TheOpenQuestionAsync(), "reject");

        Assert.True(await TestHelpers.WaitUntil(() => home.Engine.GetDownload(Id(added))!.Status == DownloadStatus.Cancelled));
        var seen = JsonDocument.Parse(await (await home.Send(HttpMethod.Get, $"/api/v1/downloads/{Id(added)}", Bob)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("cancelled", seen.GetProperty("status").GetString());
        Assert.Equal("rejected", seen.GetProperty("approval").GetString());
        Assert.Equal(0, home.Network.RequestCount);
    }

    [Fact]
    public async Task Expired_stays_pending_says_expired_and_an_admin_can_still_approve()
    {
        await using var home = await PendingFixture.StartAsync();
        var added = await home.AddAsync(Bob);

        home.Bifrost.Expire(await home.TheOpenQuestionAsync());

        Assert.True(await TestHelpers.WaitUntil(() => home.Engine.GetDownload(Id(added))!.Approval == ApprovalState.Expired));
        Assert.Equal(DownloadStatus.Pending, home.Engine.GetDownload(Id(added))!.Status);
        await Task.Delay(400);
        Assert.Empty(home.Bifrost.Open); // it does not nag: one question per start

        var byMember = await home.Send(HttpMethod.Post, $"/api/v1/downloads/{Id(added)}/approve", Bob);
        Assert.Equal(HttpStatusCode.Forbidden, byMember.StatusCode);
        var byAdmin = await home.Send(HttpMethod.Post, $"/api/v1/downloads/{Id(added)}/approve", Alice, "admin");
        Assert.Equal(HttpStatusCode.OK, byAdmin.StatusCode);
        Assert.True(await TestHelpers.WaitUntil(() => home.Engine.GetDownload(Id(added))!.Status == DownloadStatus.Completed));
        var again = await home.Send(HttpMethod.Post, $"/api/v1/downloads/{Id(added)}/approve", Alice, "admin");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task An_admin_rejecting_directly_takes_the_question_back()
    {
        await using var home = await PendingFixture.StartAsync();
        var added = await home.AddAsync(Bob);
        var question = await home.TheOpenQuestionAsync();

        var rejected = await home.Send(HttpMethod.Post, $"/api/v1/downloads/{Id(added)}/reject", Alice, "admin");

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("withdrawn", question.State);
        Assert.Equal(ApprovalState.Rejected, home.Engine.GetDownload(Id(added))!.Approval);
    }

    [Fact]
    public async Task An_admins_download_starts_at_once_with_the_default_setting()
    {
        await using var home = await PendingFixture.StartAsync();

        var added = await home.AddAsync(Alice, "admin");

        Assert.NotEqual("pending", added.GetProperty("status").GetString());
        Assert.True(await TestHelpers.WaitUntil(() => home.Engine.GetDownload(Id(added))!.Status == DownloadStatus.Completed));
        Assert.Empty(home.Bifrost.Questions);
    }

    [Theory]
    [InlineData(ApprovalRule.Everyone, "admin", true)]
    [InlineData(ApprovalRule.Everyone, "member", true)]
    [InlineData(ApprovalRule.None, "admin", false)]
    [InlineData(ApprovalRule.None, "member", false)]
    public async Task Setting_everyone_and_none(ApprovalRule rule, string role, bool pending)
    {
        await using var home = await PendingFixture.StartAsync(rule);

        var added = await home.AddAsync(Bob, role);

        Assert.Equal(pending, added.GetProperty("status").GetString() == "pending");
        if (!pending) Assert.True(await TestHelpers.WaitUntil(() => home.Engine.GetDownload(Id(added))!.Status == DownloadStatus.Completed));
        if (pending) await home.TheOpenQuestionAsync();
        else Assert.Empty(home.Bifrost.Questions);
    }

    [Fact]
    public async Task Cancelling_a_pending_download_withdraws_the_question()
    {
        await using var home = await PendingFixture.StartAsync();
        var added = await home.AddAsync(Bob);
        var question = await home.TheOpenQuestionAsync();

        var cancelled = await home.Send(HttpMethod.Post, $"/api/v1/downloads/{Id(added)}/cancel", Bob);

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("withdrawn", question.State);
        var task = home.Engine.GetDownload(Id(added))!;
        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        Assert.Null(task.Approval); // taken back by its owner, not rejected

        // A late click does nothing harmful: the download is no longer pending.
        Assert.False(await home.Engine.ApproveAsync(task.Id));
        Assert.Equal(0, home.Network.RequestCount);
    }

    [Fact]
    public async Task After_a_restart_each_pending_download_is_asked_again_once()
    {
        await using var home = await PendingFixture.StartAsync();
        var first = await home.AddAsync(Bob);
        var second = await home.AddAsync(Bob);
        Assert.True(await TestHelpers.WaitUntil(() => home.Bifrost.Open.Count() == 2));

        // Bifröst restarts under Sleipnir: its questions are gone.
        home.Bifrost.ForgetEverything();
        Assert.True(await TestHelpers.WaitUntil(() => home.Bifrost.Open.Count() == 2 && home.Bifrost.Questions.Count == 4));
        await Task.Delay(500);
        Assert.Equal(2, home.Bifrost.Open.Count());

        // Sleipnir restarts under Bifröst: it takes its questions back and asks again from its saved state.
        await home.StopSleipnirAsync();
        Assert.Empty(home.Bifrost.Open);
        await home.StartSleipnirAsync();

        Assert.True(await TestHelpers.WaitUntil(() => home.Bifrost.Open.Count() == 2));
        await Task.Delay(500);
        Assert.Equal(2, home.Bifrost.Open.Count());
        foreach (var id in new[] { Id(first), Id(second) })
        {
            Assert.Equal(DownloadStatus.Pending, home.Engine.GetDownload(id)!.Status);
            Assert.Equal(ApprovalState.Waiting, home.Engine.GetDownload(id)!.Approval);
        }
        Assert.Equal(0, home.Network.RequestCount);
    }

    [Fact]
    public async Task Alone_nothing_is_ever_pending()
    {
        await using var alone = await DaemonFixture.StartAsync();

        var response = await alone.Http.SendAsync(alone.Request(HttpMethod.Post, "/api/v1/downloads",
            JsonSerializer.Serialize(new { urls = new[] { PendingFixture.Url } })));

        var added = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("items")[0];
        Assert.Equal("queued", added.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, added.GetProperty("approval").ValueKind);
        var approve = await alone.Http.SendAsync(alone.Request(HttpMethod.Post, $"/api/v1/downloads/{Id(added)}/approve"));
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
    }

    [Fact]
    public async Task The_question_text_has_no_path()
    {
        await using var home = await PendingFixture.StartAsync();
        await home.AddAsync(Bob);

        var question = await home.TheOpenQuestionAsync();

        Assert.Equal("film.mkv from files.example.test", question.Text);
        Assert.DoesNotContain(home.Folder, question.Title + question.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/", question.Text, StringComparison.Ordinal);

        var (title, text) = ApprovalCoordinator.Question("Kid\n\u0007" + new string('x', 100),
            new DownloadTask { Url = "https://a.example/x?token=secret", Filename = "x", DestinationPath = "/var/lib/bifrost/x/sleipnir/downloads/p/x" });
        Assert.True(title.Length <= 80);
        Assert.DoesNotContain(title, c => char.IsControl(c));
        Assert.Equal("x from a.example", text); // no path, no query string
    }
}

public sealed class ExtensionSettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Without_a_file_members_wait_and_admins_do_not()
    {
        var settings = ExtensionSettings.Load(_dir.Path);

        Assert.Equal(ApprovalRule.Members, settings.Approval);
        Assert.True(settings.NeedsApproval(isAdmin: false));
        Assert.False(settings.NeedsApproval(isAdmin: true));
    }

    [Theory]
    [InlineData("members", ApprovalRule.Members)]
    [InlineData("everyone", ApprovalRule.Everyone)]
    [InlineData("none", ApprovalRule.None)]
    public void The_approval_setting_is_read(string value, ApprovalRule expected)
    {
        File.WriteAllText(_dir.File("settings.json"), $$"""{ "approval": "{{value}}" }""");

        Assert.Equal(expected, ExtensionSettings.Load(_dir.Path).Approval);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "approval": "sometimes" }""")]
    [InlineData("""{ "approval": 3 }""")]
    public void A_broken_file_falls_back_to_the_strict_default_and_says_so(string content)
    {
        File.WriteAllText(_dir.File("settings.json"), content);
        var warnings = new List<string>();

        var settings = ExtensionSettings.Load(_dir.Path, warnings.Add);

        Assert.Equal(ApprovalRule.Members, settings.Approval);
        Assert.Contains("settings.json", Assert.Single(warnings), StringComparison.Ordinal);
    }
}
