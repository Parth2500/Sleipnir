using System.Collections.Concurrent;
using Sleipnir.Core.Interfaces;
using Sleipnir.Core.Models;
using Sleipnir.Server.Hosting;

namespace Sleipnir.Server.Bifrost;

/// <summary>
/// Pending downloads (spec §7.4): asks Bifröst's admin about each one, waits, and acts on the answer.
/// Approve lets the download start, reject cancels it, and an expired question leaves it pending.
/// Questions live in Bifröst's memory, so every pending download is asked about again at start.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed partial class ApprovalCoordinator(IDownloadService downloads, BifrostApiClient bifrost, ExtensionSettings settings, ILogger logger)
    : IAsyncDisposable
{
    private static readonly TimeSpan QuestionLife = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, Asking> _asking = new();
    private readonly CancellationTokenSource _stop = new();

    private sealed class Asking
    {
        public CancellationTokenSource Cancel { get; } = new();
        public Task Work { get; set; } = Task.CompletedTask;
        public Guid? QuestionId { get; set; }
    }

    /// <summary>Whether a download this caller adds must wait for approval.</summary>
    public bool NeedsApproval(bool isAdmin) => settings.NeedsApproval(isAdmin);

    /// <summary>Asks about every download still pending. Called once, after the engine restored its downloads.</summary>
    public void Start()
    {
        foreach (var task in downloads.GetAllDownloads().Where(d => d.Status == DownloadStatus.Pending).ToList()) Track(task.Id);
    }

    /// <summary>Starts asking about a pending download. Asking twice about one download does nothing.</summary>
    public void Track(Guid downloadId)
    {
        if (_stop.IsCancellationRequested) return;
        var asking = new Asking();
        if (!_asking.TryAdd(downloadId, asking))
        {
            asking.Cancel.Dispose();
            return;
        }
        asking.Work = Task.Run(() => AskAndActAsync(downloadId, asking));
    }

    /// <summary>
    /// The download was decided some other way (its owner cancelled it, or an admin approved or
    /// rejected it directly): the question is taken back, so it leaves every screen.
    /// </summary>
    public async Task SettledAsync(Guid downloadId)
    {
        if (!_asking.TryRemove(downloadId, out var asking)) return;
        await asking.Cancel.CancelAsync();
        try { await asking.Work; }
        catch (OperationCanceledException) { }
        await WithdrawAsync(asking);
        asking.Cancel.Dispose();
    }

    /// <summary>The question to ask. The text names the site and the file, never a folder on the home machine.</summary>
    public static (string Title, string Text) Question(string? personName, DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var who = Clean(personName is { Length: > 0 } ? personName : "someone", 40);
        var host = Uri.TryCreate(task.Url, UriKind.Absolute, out var uri) ? uri.Host : "an unknown site";
        return ($"Download for {who}?", Clean($"{Path.GetFileName(task.Filename)} from {host}", 500));
    }

    private static string Clean(string text, int max)
    {
        var plain = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return plain.Length <= max ? plain : plain[..(max - 1)] + "…";
    }

    private async Task AskAndActAsync(Guid downloadId, Asking asking)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(asking.Cancel.Token, _stop.Token);
        var ct = linked.Token;
        var retry = TimeSpan.FromSeconds(1);
        try
        {
            while (true)
            {
                if (downloads.GetDownload(downloadId) is not { Status: DownloadStatus.Pending } task) return;
                try
                {
                    if (asking.QuestionId is null)
                    {
                        var name = task.OwnerId is { } owner ? await bifrost.PersonNameAsync(owner, ct) : null;
                        var (title, text) = Question(name, task);
                        asking.QuestionId = await bifrost.AskAsync(title, text, DateTime.UtcNow + QuestionLife, ct);
                    }

                    var (state, choice) = await bifrost.WaitAsync(asking.QuestionId.Value, ct);
                    asking.QuestionId = null; // closed: nothing left to take back
                    switch (state, choice)
                    {
                        case ("answered", "approve"):
                            await downloads.ApproveAsync(downloadId);
                            return;
                        case ("answered", "reject"):
                            await downloads.RejectAsync(downloadId);
                            return;
                        case ("expired", _):
                            await downloads.MarkApprovalExpiredAsync(downloadId);
                            return;
                        case ("gone", _):
                            continue; // Bifröst restarted and forgot it: ask again
                        default:
                            return; // withdrawn, or an answer this version does not know: leave it pending
                    }
                }
                catch (Exception e) when (e is HttpRequestException or IOException or System.Text.Json.JsonException
                    || (e is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    LogUnreachable(logger, e.Message, retry.TotalSeconds);
                    asking.QuestionId = null; // if Bifröst restarted, the question is gone with it
                    await Task.Delay(retry, ct);
                    retry = retry * 2 < MaxRetryDelay ? retry * 2 : MaxRetryDelay;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Settled another way, or stopping.
        }
        finally
        {
            // Finished on its own: forget it, unless SettledAsync already took it.
            if (!asking.Cancel.IsCancellationRequested && !_stop.IsCancellationRequested
                && _asking.TryRemove(new KeyValuePair<Guid, Asking>(downloadId, asking)))
            {
                asking.Cancel.Dispose();
            }
        }
    }

    private async Task WithdrawAsync(Asking asking)
    {
        if (asking.QuestionId is not { } question) return;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await bifrost.WithdrawAsync(question, limit.Token);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            // Bifröst is gone or slow. A late answer finds the download no longer pending and does nothing.
        }
    }

    /// <summary>Stops asking and takes the open questions back, so a restart of Sleipnir alone leaves no duplicates.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        await _stop.CancelAsync();
        foreach (var asking in _asking.Values)
        {
            try { await asking.Work; }
            catch (OperationCanceledException) { }
            await WithdrawAsync(asking);
            asking.Cancel.Dispose();
        }
        _asking.Clear();
        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot ask Bifröst's admin ({Reason}); trying again in {Seconds} s")]
    private static partial void LogUnreachable(ILogger logger, string reason, double seconds);
}
