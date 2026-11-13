using System.Text;
using System.Text.Json;
using Sleipnir.Core.Api;
using Sleipnir.Core.Interfaces;

namespace Sleipnir.Server.Api;

internal sealed class DownloadEndpoints(IDownloadService downloads, string defaultDestination)
{
    private const int DefaultLimit = 50, MaxLimit = 200, MaxUrls = 100, MaxKeyLength = 200;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly IdempotencyCache<PageDto<DownloadDto>> _idempotency = new();
    private readonly SemaphoreSlim _addGate = new(1, 1);

    public void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/downloads", List);
        api.MapPost("/downloads", Add);
        api.MapGet("/downloads/{id:guid}", (Guid id) => Found(id, task => Results.Ok(DownloadDto.From(task, includeSegments: true))));
        api.MapPost("/downloads/{id:guid}/pause", (Guid id) => Control(id, downloads.PauseDownloadAsync));
        api.MapPost("/downloads/{id:guid}/resume", (Guid id) => Control(id, downloads.ResumeDownloadAsync));
        api.MapPost("/downloads/{id:guid}/cancel", (Guid id) => Control(id, downloads.CancelDownloadAsync));
        api.MapDelete("/downloads/{id:guid}", Remove);
    }

    private IResult Found(Guid id, Func<Core.Models.DownloadTask, IResult> then) =>
        downloads.GetDownload(id) is { } task ? then(task) : NotFound();

    private static IResult NotFound() => Problem.Create(404, "download_not_found", "No such download.");

    private IResult List(string? limit, string? cursor)
    {
        var take = DefaultLimit;
        if (limit is not null)
        {
            if (!int.TryParse(limit, out var parsed)) return Problem.Create(400, "invalid_request", "limit must be a number.");
            take = Math.Clamp(parsed, 1, MaxLimit);
        }

        (long Ticks, Guid Id)? after = null;
        if (cursor is not null)
        {
            if (!TryDecodeCursor(cursor, out var decoded)) return Problem.Create(400, "invalid_cursor", "The cursor is not valid.");
            after = decoded;
        }

        var ordered = downloads.GetAllDownloads().OrderBy(d => d.CreatedAt.Ticks).ThenBy(d => d.Id);
        var rest = after is { } a
            ? ordered.Where(d => d.CreatedAt.Ticks > a.Ticks || (d.CreatedAt.Ticks == a.Ticks && d.Id.CompareTo(a.Id) > 0))
            : ordered;
        var page = rest.Take(take + 1).ToList();

        var items = page.Take(take).ToList();
        var next = page.Count > take ? EncodeCursor(items[^1].CreatedAt.Ticks, items[^1].Id) : null;
        return Results.Ok(new PageDto<DownloadDto>(items.Select(d => DownloadDto.From(d)).ToArray(), next));
    }

    private async Task<IResult> Add(HttpRequest request)
    {
        string body;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(); // Kestrel's 1 MB limit throws here; the host turns it into a 413.
        }

        AddDownloadsRequest? parsed;
        try { parsed = JsonSerializer.Deserialize<AddDownloadsRequest>(body, Web); }
        catch (JsonException) { parsed = null; }
        if (parsed is null) return Problem.Create(400, "invalid_request", "The body must be a JSON object with a urls array.");

        var urls = parsed.Urls ?? [];
        if (urls.Length is 0 or > MaxUrls || urls.Any(u => !IsHttpUrl(u)))
        {
            return Problem.Create(400, "invalid_url", $"Provide 1–{MaxUrls} absolute http or https URLs.");
        }

        var key = request.Headers["Idempotency-Key"].ToString();
        if (key.Length > MaxKeyLength) return Problem.Create(400, "invalid_request", "Idempotency-Key is too long.");
        if (key.Length == 0) return Results.Json(await AddAll(urls, parsed.Destination), statusCode: 201);

        var hash = IdempotencyCache<PageDto<DownloadDto>>.Hash(body);
        await _addGate.WaitAsync();
        try
        {
            switch (_idempotency.TryGet(key, hash, out var cached))
            {
                case IdempotencyCache<PageDto<DownloadDto>>.Lookup.Hit:
                    return Results.Json(cached, statusCode: 201);
                case IdempotencyCache<PageDto<DownloadDto>>.Lookup.KeyReused:
                    return Problem.Create(422, "idempotency_key_reused", "That Idempotency-Key was used for a different request.");
            }

            var page = await AddAll(urls, parsed.Destination);
            _idempotency.Store(key, hash, page);
            return Results.Json(page, statusCode: 201);
        }
        finally
        {
            _addGate.Release();
        }
    }

    private async Task<PageDto<DownloadDto>> AddAll(string[] urls, string? destination)
    {
        var folder = string.IsNullOrWhiteSpace(destination) ? defaultDestination : destination;
        var added = new List<DownloadDto>();
        foreach (var url in urls)
        {
            var id = await downloads.AddDownloadAsync(url, folder);
            if (downloads.GetDownload(id) is { } task) added.Add(DownloadDto.From(task));
        }
        return new PageDto<DownloadDto>(added.ToArray(), null);
    }

    private async Task<IResult> Control(Guid id, Func<Guid, Task> action)
    {
        if (downloads.GetDownload(id) is null) return NotFound();
        await action(id);
        return downloads.GetDownload(id) is { } task ? Results.Ok(DownloadDto.From(task)) : NotFound();
    }

    private async Task<IResult> Remove(Guid id)
    {
        if (downloads.GetDownload(id) is null) return NotFound();
        await downloads.RemoveDownloadAsync(id);
        return downloads.GetDownload(id) is null
            ? Results.NoContent()
            : Problem.Create(409, "download_not_finished", "Only completed or cancelled downloads can be removed.");
    }

    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static string EncodeCursor(long ticks, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ticks}:{id}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecodeCursor(string cursor, out (long Ticks, Guid Id) result)
    {
        result = default;
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || !Guid.TryParse(parts[1], out var id)) return false;
            result = (ticks, id);
            return true;
        }
        catch (FormatException) { return false; }
    }
}
