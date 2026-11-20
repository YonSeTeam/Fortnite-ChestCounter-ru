#if ONLINE
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
#endif

namespace ChestCounter;

/// <summary>State of the online exchange, for the window.</summary>
public sealed record OnlineStatus(bool Available, bool Enabled, bool ServerSet, string? Error, DateTime? LastOkUtc, int Sent);

#if ONLINE
/// <summary>
/// ONLINE build only. After each match the player's count is sent to the squad server, and the counts of the
/// teammates who did the same are fetched back and imported as squad cards (same checks as a pasted code). Nothing
/// is sent unless the player turned it on. See server/README.md for what the server keeps.
/// </summary>
public static class OnlineSync
{
    public static bool Available => true;

    /// <summary>Address built into the exe at publish time (-p:ServerUrl=...), if any. Never stored in the repository.</summary>
    public static string DefaultServer { get; } = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "ServerUrl")?.Value ?? "";

    private sealed record Result(string? Error, List<SquadCard>? Cards, List<string>? Uploaded);

    private static readonly ConcurrentQueue<Result> Results = new();
    private static int _busy;
    private static DateTime _nextFetchUtc = DateTime.MinValue;
    private static string? _error;
    private static DateTime? _lastOk;
    private static int _sent;

    private static readonly TimeSpan UploadWindow = TimeSpan.FromDays(3);
    private static readonly TimeSpan FetchWindow = TimeSpan.FromHours(48);

    /// <summary>The server to use: what the player typed, else the built-in address. Only https (or a local address for tests).</summary>
    public static string? Server(Config c)
    {
        var raw = (string.IsNullOrWhiteSpace(c.OnlineServer) ? DefaultServer : c.OnlineServer).Trim().TrimEnd('/');
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return null;
        }
        var local = uri.IsLoopback;
        return uri.Scheme == Uri.UriSchemeHttps || (local && uri.Scheme == Uri.UriSchemeHttp) ? raw : null;
    }

    public static OnlineStatus Status(Config c) => new(true, c.OnlineEnabled, Server(c) != null, _error, _lastOk, _sent);

    private static DateTime When(MatchRecord m) => m.MatchStartUtc ?? m.AnalyzedAtUtc;

    private static bool IsRecent(MatchRecord m, TimeSpan window) => DateTime.UtcNow - When(m) < window;

    /// <summary>Teammates that are real players with a known Epic id.</summary>
    private static IEnumerable<TeamChestCount> Humans(MatchRecord m) => m.TeamChests.Where(t => !t.IsBot && !string.IsNullOrEmpty(t.EpicId));

    /// <summary>Called by the main loop (data is only touched there). True when the data changed and must be redrawn.</summary>
    public static bool Tick(Config config, TrackerData data, Store store)
    {
        var changed = false;
        while (Results.TryDequeue(out var r))
        {
            if (r.Error != null)
            {
                _error = r.Error;
                continue;
            }
            _error = null;
            _lastOk = DateTime.UtcNow;
            if (r.Uploaded is { Count: > 0 })
            {
                foreach (var id in r.Uploaded.Where(id => !data.OnlineSent.Contains(id)))
                {
                    data.OnlineSent.Add(id);
                }
                _sent += r.Uploaded.Count;
                store.SaveData(data);
            }
            if (r.Cards is { Count: > 0 })
            {
                var res = Squad.ImportCards(data, r.Cards);
                if (res.Added + res.Updated > 0)
                {
                    store.SaveData(data);
                    changed = true;
                }
            }
        }

        var server = Server(config);
        if (!config.OnlineEnabled || server == null || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return changed;
        }

        List<object> uploads;
        List<string> uploadIds;
        string[] epics;
        bool fetch;
        try
        {
            if (string.IsNullOrEmpty(config.OnlineKey))
            {
                config.OnlineKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                store.SaveConfig(config);
            }

            // Matches of the player (their own replays), finished, not sent yet.
            var toSend = data.Matches
                .Where(m => IsRecent(m, UploadWindow) && !data.OnlineSent.Contains(m.SessionId))
                .Select(m => (Match: m, Card: Squad.Build(m)))
                .Where(x => x.Card != null)
                .OrderBy(x => When(x.Match))
                .Take(20)
                .ToList();
            uploads = toSend.Select(x => (object)new
            {
                epic = x.Card!.Epic,
                name = x.Card.Name,
                match = x.Card.Match,
                start = (x.Card.Start ?? When(x.Match)).ToUniversalTime().ToString("o"),
                chests = x.Card.Chests,
                destroyed = x.Card.Destroyed,
                mates = Humans(x.Match).Select(t => t.EpicId!).Distinct().ToArray(),
            }).ToList();
            uploadIds = toSend.Select(x => x.Card!.Match).ToList();

            // Fetch while a recent match still has a teammate without an exact count: often right after the match
            // (the teammate's counter finishes about as quickly), then now and then for the next two days.
            var open = data.Matches.Where(m => IsRecent(m, FetchWindow) && m.IsReplayOwner && Humans(m).Any(t => Squad.CardFor(data.SquadCards, m, t) == null)).ToList();
            var fresh = open.Any(m => DateTime.UtcNow - When(m) < TimeSpan.FromMinutes(40));
            fetch = (open.Count > 0 && DateTime.UtcNow >= _nextFetchUtc) || uploads.Count > 0;
            if (fetch)
            {
                _nextFetchUtc = DateTime.UtcNow + (fresh ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10));
            }
            epics = data.Matches.Where(m => m.IsReplayOwner && IsRecent(m, FetchWindow) && !string.IsNullOrEmpty(m.EpicId)).Select(m => m.EpicId!).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
        }
        catch
        {
            Volatile.Write(ref _busy, 0);
            throw;
        }

        if (uploads.Count == 0 && !fetch)
        {
            Volatile.Write(ref _busy, 0);
            return changed;
        }

        var key = config.OnlineKey!;
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"ChestCounter-Online/{AppInfo.VersionText}");
                http.DefaultRequestHeaders.Add("X-Install-Key", key);

                var done = new List<string>();
                for (var i = 0; i < uploads.Count; i++)
                {
                    using var body = new StringContent(JsonSerializer.Serialize(uploads[i]), Encoding.UTF8, "application/json");
                    using var resp = await http.PostAsync(server + "/v1/report", body);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Results.Enqueue(new Result(await Explain(resp), null, done));
                        return;
                    }
                    done.Add(uploadIds[i]);
                }

                var cards = new List<SquadCard>();
                if (fetch)
                {
                    foreach (var epic in epics)
                    {
                        using var resp = await http.GetAsync($"{server}/v1/mates?epic={Uri.EscapeDataString(epic)}");
                        if (!resp.IsSuccessStatusCode)
                        {
                            Results.Enqueue(new Result(await Explain(resp), null, done));
                            return;
                        }
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                        foreach (var c in doc.RootElement.GetProperty("cards").EnumerateArray())
                        {
                            cards.Add(new SquadCard
                            {
                                Match = c.GetProperty("match").GetString() ?? "",
                                Epic = c.GetProperty("epic").GetString() ?? "",
                                Name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                                Chests = c.GetProperty("chests").GetInt32(),
                                Destroyed = c.GetProperty("destroyed").GetInt32(),
                            });
                        }
                    }
                }
                Results.Enqueue(new Result(null, cards, done));
            }
            catch (Exception ex)
            {
                Results.Enqueue(new Result(ex is TaskCanceledException ? L.T("le serveur ne répond pas", "the server does not answer", "el servidor no responde", "сервер не отвечает") : ex.Message, null, null));
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        });
        return changed;
    }

    private static async Task<string> Explain(HttpResponseMessage resp)
    {
        if ((int)resp.StatusCode == 403)
        {
            return L.T("cet identifiant Epic est lié à une autre installation", "this Epic id is tied to another installation", "este id de Epic está ligado a otra instalación", "этот Epic ID привязан к другой установке");
        }
        string text;
        try { text = await resp.Content.ReadAsStringAsync(); } catch { text = ""; }
        return $"HTTP {(int)resp.StatusCode} {text}".Trim();
    }
}
#else
/// <summary>The normal build has no online exchange: no network code, nothing is sent.</summary>
public static class OnlineSync
{
    public static bool Available => false;
    public static string DefaultServer => "";
    public static string? Server(Config c) => null;
    public static OnlineStatus Status(Config c) => new(false, false, false, null, null, 0);
    public static bool Tick(Config config, TrackerData data, Store store) => false;
}
#endif
