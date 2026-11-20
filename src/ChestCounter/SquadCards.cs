using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ChestCounter;

/// <summary>
/// The exact chest count of one player in one match, as read in THAT player's own replay. A replay only holds what
/// happened within about 150 m of the recording player, so the count of a teammate seen from another replay is an
/// estimate; the teammate's own number is exact. Cards come from the online exchange (see OnlineSync).
/// Rewards (treasures) are never part of a card.
/// </summary>
public sealed class SquadCard
{
    /// <summary>Match id (the replay's game session id): the same for every player of the match.</summary>
    [JsonPropertyName("m")] public string Match { get; set; } = "";

    /// <summary>Epic id of the player the numbers belong to (the sender).</summary>
    [JsonPropertyName("e")] public string Epic { get; set; } = "";

    /// <summary>Display name of the sender, to show who the card came from.</summary>
    [JsonPropertyName("n")] public string Name { get; set; } = "";

    /// <summary>Chests opened or destroyed, counted with the same rules as the sender's own total.</summary>
    [JsonPropertyName("c")] public int Chests { get; set; }

    /// <summary>How many of those were destroyed rather than opened.</summary>
    [JsonPropertyName("d")] public int Destroyed { get; set; }

    /// <summary>
    /// Prize ids of the treasures the sender obtained in this match, only when the sender chose to share rewards
    /// (null otherwise).
    /// </summary>
    [JsonPropertyName("f")] public List<string>? Finds { get; set; }

    /// <summary>Start of the match (UTC), informative only.</summary>
    [JsonPropertyName("s")] public DateTime? Start { get; set; }

    /// <summary>When the card was imported here (not part of the code).</summary>
    [JsonPropertyName("i")] public DateTime? ImportedUtc { get; set; }
}

public sealed record SquadImportResult(int Added, int Updated, int Unchanged, int NoMatch, int NotSquad, int Own, int Invalid, int BadCodes)
{
    public int Accepted => Added + Updated + Unchanged;
    public int Rejected => NoMatch + NotSquad + Own + Invalid + BadCodes;
}

public static class Squad
{
    private const int MaxChestsPerMatch = 300;
    private const int MaxFinds = 30;
    private static readonly Regex FindRegex = new(@"^[A-Za-z0-9_:.\-]{1,80}$", RegexOptions.Compiled);
    private static readonly Regex IdRegex = new(@"^[A-Za-z0-9\-]{8,64}$", RegexOptions.Compiled);

    /// <summary>Key of a card: one per (match, player). A second import of the same pair replaces the first.</summary>
    public static string Key(string match, string epic) => match.Trim().ToLowerInvariant() + "|" + epic.Trim().ToLowerInvariant();

    private static bool Same(string? a, string? b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The card of the player for a match they recorded (null when it cannot be made: not their replay, no ids).</summary>
    public static SquadCard? Build(MatchRecord m, bool shareRewards = false)
    {
        if (!m.IsReplayOwner || string.IsNullOrEmpty(m.EpicId) || !IdRegex.IsMatch(m.EpicId) || !IdRegex.IsMatch(m.SessionId ?? "") || !GameModes.Counts(m))
        {
            return null;
        }
        return new SquadCard
        {
            Match = m.SessionId,
            Epic = m.EpicId,
            Name = m.PlayerName ?? "",
            Chests = m.ChestsOpened,
            Destroyed = m.Chests.Count(c => c.Destroyed),
            Start = m.MatchStartUtc,
            Finds = shareRewards ? m.TreasureFinds.Select(f => f.PrizeId).Where(id => !string.IsNullOrEmpty(id) && FindRegex.IsMatch(id!)).Select(id => id!).Take(MaxFinds).ToList() : null,
        };
    }

    private static bool IsValid(SquadCard c) =>
        c.Match != null && c.Epic != null && IdRegex.IsMatch(c.Match) && IdRegex.IsMatch(c.Epic)
        && c.Chests is >= 0 and <= MaxChestsPerMatch && c.Destroyed >= 0 && c.Destroyed <= c.Chests
        && (c.Finds == null || (c.Finds.Count <= MaxFinds && c.Finds.All(f => f != null && FindRegex.IsMatch(f))));

    /// <summary>
    /// Adds the cards received from the online exchange to the data. A card is kept only when its match is in this
    /// history and its sender was in the player's squad there (listed as a teammate, not a bot); a card of the player
    /// themself is ignored. Receiving the same card again replaces it, it never adds up.
    /// </summary>
    public static SquadImportResult ImportCards(TrackerData d, IEnumerable<SquadCard> cards)
    {
        int added = 0, updated = 0, unchanged = 0, noMatch = 0, notSquad = 0, own = 0, invalid = 0;
        var seen = new HashSet<string>();
        foreach (var card in cards)
        {
            if (!IsValid(card))
            {
                invalid++;
                continue;
            }
            if (!seen.Add(Key(card.Match, card.Epic)))
            {
                continue; // the same card twice in one paste
            }
            var matches = d.Matches.Where(m => Same(m.SessionId, card.Match)).ToList();
            if (matches.Count == 0)
            {
                noMatch++;
            }
            else if (matches.Any(m => Same(m.EpicId, card.Epic)))
            {
                own++;
            }
            else if (!matches.Any(m => m.TeamChests.Any(t => !t.IsBot && Same(t.EpicId, card.Epic))))
            {
                notSquad++;
            }
            else
            {
                var key = Key(card.Match, card.Epic);
                var old = d.SquadCards.FirstOrDefault(x => Key(x.Match, x.Epic) == key);
                var finds = card.Finds is { Count: > 0 } ? card.Finds.ToList() : null;
                if (old != null && old.Chests == card.Chests && old.Destroyed == card.Destroyed
                    && (old.Finds ?? new List<string>()).SequenceEqual(finds ?? new List<string>()))
                {
                    unchanged++;
                    continue;
                }
                d.SquadCards.RemoveAll(x => Key(x.Match, x.Epic) == key);
                d.SquadCards.Add(new SquadCard
                {
                    Match = card.Match.Trim(),
                    Epic = card.Epic.Trim(),
                    Name = (card.Name ?? "").Trim() is { Length: > 40 } n ? n[..40] : (card.Name ?? "").Trim(),
                    Chests = card.Chests,
                    Destroyed = card.Destroyed,
                    Finds = finds,
                    Start = card.Start,
                    ImportedUtc = DateTime.UtcNow,
                });
                if (old == null) added++; else updated++;
            }
        }
        return new SquadImportResult(added, updated, unchanged, noMatch, notSquad, own, invalid, 0);
    }

    /// <summary>Chests of the teammates who handed an exact count over (what "you + your friends" adds to your own total).</summary>
    public static int ExactChests(IEnumerable<MatchRecord> matches, IReadOnlyList<SquadCard> cards) =>
        matches.Sum(m => m.TeamChests.Sum(t => CardFor(cards, m, t)?.Chests ?? 0));

    /// <summary>One treasure a friend shared: who, in which match, which prize.</summary>
    public sealed record FriendFind(string Friend, MatchRecord Match, string PrizeId);

    /// <summary>Treasures that friends chose to share, newest match first.</summary>
    public static List<FriendFind> FriendFinds(IEnumerable<MatchRecord> matches, IReadOnlyList<SquadCard> cards)
    {
        var list = new List<FriendFind>();
        foreach (var m in matches.OrderByDescending(m => m.MatchStartUtc ?? m.AnalyzedAtUtc))
        {
            foreach (var t in m.TeamChests)
            {
                if (CardFor(cards, m, t) is { Finds: { Count: > 0 } finds } card)
                {
                    list.AddRange(finds.Select(f => new FriendFind(string.IsNullOrEmpty(card.Name) ? t.Name : card.Name, m, f)));
                }
            }
        }
        return list;
    }

    /// <summary>The card of a teammate for a match, when one was imported.</summary>
    public static SquadCard? CardFor(IReadOnlyList<SquadCard> cards, MatchRecord m, TeamChestCount mate)
    {
        if (mate.IsBot || string.IsNullOrEmpty(mate.EpicId) || cards.Count == 0)
        {
            return null;
        }
        return cards.FirstOrDefault(c => Same(c.Match, m.SessionId) && Same(c.Epic, mate.EpicId));
    }

    /// <summary>Chests of the teammates in a match: exact where a card was imported, else what the replay holds.</summary>
    public static int TeamChestsOf(MatchRecord m, IReadOnlyList<SquadCard> cards) =>
        m.TeamChests.Sum(t => CardFor(cards, m, t)?.Chests ?? t.Chests);

    /// <summary>Teammates (players, not bots) over all the matches: how many have an exact count, out of how many.</summary>
    public static (int Exact, int Total) Coverage(IEnumerable<MatchRecord> matches, IReadOnlyList<SquadCard> cards)
    {
        int exact = 0, total = 0;
        foreach (var m in matches)
        {
            foreach (var t in m.TeamChests.Where(t => !t.IsBot))
            {
                total++;
                if (CardFor(cards, m, t) != null)
                {
                    exact++;
                }
            }
        }
        return (exact, total);
    }
}
