using System.Text.Json.Nodes;

namespace Abacus;

/// <summary>
/// A recurring wall-clock interval during which Abacus refuses to start a new
/// ticket. Windows are a block list: every hour outside them stays claimable, so
/// a provider's expensive "peak" hours are recorded rather than the long
/// off-peak remainder.
/// </summary>
public sealed record ClaimWindow(string Spec, IReadOnlySet<DayOfWeek> Days, TimeSpan Start, TimeSpan End)
{
    /// <summary>A window whose end precedes its start runs into the following day.</summary>
    public bool Wraps => End < Start;

    public bool Contains(DayOfWeek day, TimeSpan timeOfDay)
    {
        if (Days.Contains(day))
        {
            if (Wraps)
            {
                if (timeOfDay >= Start) return true;
            }
            else if (timeOfDay >= Start && timeOfDay < End)
            {
                return true;
            }
        }

        // The tail of a window that began on the previous day belongs to this day.
        return Wraps && Days.Contains(PreviousDay(day)) && timeOfDay < End;
    }

    public static ClaimWindow Parse(string spec, string field)
    {
        var trimmed = spec.Trim();
        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 2)
        {
            throw new OptionsException($"{field}: '{trimmed}' must look like '<days> <from>-<to>', for example 'mon-fri 01:00-04:00'");
        }

        var days = ParseDays(tokens[0], trimmed, field);
        var range = tokens[1].Split('-');
        if (range.Length != 2)
        {
            throw new OptionsException($"{field}: '{trimmed}' must look like '<days> <from>-<to>', for example 'mon-fri 01:00-04:00'");
        }

        var start = ParseTime(range[0], trimmed, field);
        var end = ParseTime(range[1], trimmed, field);
        if (start == end)
        {
            throw new OptionsException($"{field}: '{trimmed}' must start and end at different times");
        }

        return new ClaimWindow(trimmed, days, start, end);
    }

    private static IReadOnlySet<DayOfWeek> ParseDays(string text, string spec, string field)
    {
        text = text.Trim().ToLowerInvariant();
        if (text is "*" or "daily" or "everyday" || text.Equals("every-day", StringComparison.OrdinalIgnoreCase))
        {
            return Enum.GetValues<DayOfWeek>().ToHashSet();
        }

        var days = new HashSet<DayOfWeek>();
        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var range = token.Split('-');
            if (range.Length > 2)
            {
                throw new OptionsException($"{field}: '{spec}' has an invalid day list '{text}'");
            }

            var first = ParseDay(range[0], spec, field);
            if (range.Length == 1)
            {
                days.Add(first);
                continue;
            }

            var last = ParseDay(range[1], spec, field);
            for (var offset = 0; offset < 7; offset++)
            {
                var day = (DayOfWeek)(((int)first + offset) % 7);
                days.Add(day);
                if (day == last) break;
            }
        }

        if (days.Count == 0) throw new OptionsException($"{field}: '{spec}' does not name any days");
        return days;
    }

    private static DayOfWeek ParseDay(string text, string spec, string field)
    {
        var name = text.Trim().ToLowerInvariant();
        for (var index = 0; index < 7; index++)
        {
            var day = (DayOfWeek)index;
            var full = day.ToString().ToLowerInvariant();
            if (name == full || (name.Length == 3 && name == full[..3]))
            {
                return day;
            }
        }

        throw new OptionsException($"{field}: '{spec}' has an unknown day '{text}'; use mon, tue, wed, thu, fri, sat, sun, or a range such as mon-fri");
    }

    private static TimeSpan ParseTime(string text, string spec, string field)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var hours)
            || !int.TryParse(parts[1], out var minutes)
            || hours is < 0 or > 23
            || minutes is < 0 or > 59)
        {
            throw new OptionsException($"{field}: '{spec}' has an invalid time '{text}'; use a 24-hour HH:MM value such as 01:00");
        }

        return new TimeSpan(hours, minutes, 0);
    }

    private static DayOfWeek PreviousDay(DayOfWeek day) => (DayOfWeek)(((int)day + 6) % 7);

    public static string FormatTime(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}";
}

/// <summary>
/// Claim windows plus the minimum time a window must have left before a ticket may
/// start, so a long ticket is not launched minutes before the cheap hours end.
/// </summary>
public sealed class ClaimSchedule
{
    public const string ConfigurationName = "schedule";

    /// <summary>How far ahead transitions and openings are searched.</summary>
    private static readonly TimeSpan Horizon = TimeSpan.FromDays(16);
    private static readonly TimeSpan CoarseStep = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FineStep = TimeSpan.FromMinutes(1);

    private ClaimSchedule(TimeZoneInfo zone, IReadOnlyList<ClaimWindow> windows, TimeSpan minWindowRemaining)
    {
        Zone = zone;
        BlockedWindows = windows;
        MinWindowRemaining = minWindowRemaining;
    }

    public TimeZoneInfo Zone { get; }
    public IReadOnlyList<ClaimWindow> BlockedWindows { get; }
    public TimeSpan MinWindowRemaining { get; }

    public bool IsBlockedAt(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, Zone);
        return BlockedWindows.Any(window => window.Contains(local.DayOfWeek, local.TimeOfDay));
    }

    /// <summary>
    /// Whether a ticket may start now. The reason describes why not, without the
    /// recovery time, so callers can append their own next-claimable detail.
    /// </summary>
    public bool CanClaimAt(DateTimeOffset instant, out string reason)
    {
        if (IsBlockedAt(instant))
        {
            reason = "inside a blocked schedule window";
            return false;
        }

        var remaining = RemainingAt(instant);
        if (remaining is { } left && left < MinWindowRemaining)
        {
            reason = $"the window ends in {FormatDuration(left)} but minWindowRemaining is {FormatDuration(MinWindowRemaining)}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Time until the next claim-blocking window begins, or null when claims are
    /// blocked now or the schedule never blocks again within the search horizon.
    /// </summary>
    public TimeSpan? RemainingAt(DateTimeOffset instant) =>
        !IsBlockedAt(instant)
            && NextChange(instant, static (schedule, candidate) => schedule.IsBlockedAt(candidate)) is { } start
            ? start - instant
            : null;

    /// <summary>
    /// The next instant at which a ticket may start, or null when no scheduled
    /// window can satisfy the configured minimum remaining time.
    /// </summary>
    public DateTimeOffset? NextClaimableAt(DateTimeOffset instant)
    {
        if (CanClaimAt(instant, out _)) return instant;
        return NextChange(instant, static (schedule, candidate) => schedule.CanClaimAt(candidate, out _));
    }

    public string DescribeAt(DateTimeOffset instant)
    {
        if (CanClaimAt(instant, out var reason))
        {
            return RemainingAt(instant) is { } remaining
                ? $"claims are open; the next blocked window starts in {FormatDuration(remaining)}"
                : "claims are open";
        }

        var opening = NextClaimableAt(instant);
        return opening is { } next
            ? $"{reason}; claims resume {FormatLocal(next)} (in {FormatDuration(next - instant)})"
            : $"{reason}; no later scheduled window allows a claim";
    }

    public IReadOnlyList<string> Warnings()
    {
        var windows = new List<string>();
        if (BlockedWindows.Count == 0) return windows;
        var longest = LongestClaimableRun();
        if (MinWindowRemaining > longest)
        {
            windows.Add(
                $"minWindowRemaining {FormatDuration(MinWindowRemaining)} is longer than the longest scheduled window "
                + $"({FormatDuration(longest)}), so scheduled claims can never start");
        }

        return windows;
    }

    /// <summary>Wall-clock length of the longest stretch the schedule leaves unblocked.</summary>
    private TimeSpan LongestClaimableRun()
    {
        var minutes = new bool[7 * 24 * 60];
        for (var day = 0; day < 7; day++)
        {
            for (var minute = 0; minute < 24 * 60; minute++)
            {
                minutes[(day * 24 * 60) + minute] = BlockedWindows.Any(window =>
                    window.Contains((DayOfWeek)day, TimeSpan.FromMinutes(minute)));
            }
        }

        // Windows repeat weekly, so a week-long wrap-around scan covers every run.
        var longest = 0;
        var run = 0;
        for (var step = 0; step < minutes.Length * 2; step++)
        {
            if (minutes[step % minutes.Length])
            {
                run = 0;
            }
            else
            {
                run++;
                longest = Math.Max(longest, Math.Min(run, minutes.Length));
            }
        }

        return TimeSpan.FromMinutes(longest);
    }

    /// <summary>
    /// Earliest instant after <paramref name="instant"/> where the predicate's value
    /// differs from its value at <paramref name="instant"/>. Searching is coarse then
    /// fine: a transition shorter than the coarse step only delays the reported time,
    /// because callers re-check the predicate itself before acting on it.
    /// </summary>
    private DateTimeOffset? NextChange(DateTimeOffset instant, Func<ClaimSchedule, DateTimeOffset, bool> predicate)
    {
        var initial = predicate(this, instant);
        var limit = instant + Horizon;
        DateTimeOffset? hit = null;
        for (var candidate = instant + CoarseStep; candidate <= limit; candidate += CoarseStep)
        {
            if (predicate(this, candidate) == initial) continue;
            hit = candidate;
            break;
        }

        if (hit is null) return null;
        for (var candidate = hit.Value - CoarseStep; candidate < hit.Value; candidate += FineStep)
        {
            if (candidate > instant && predicate(this, candidate) != initial) return candidate;
        }

        return hit;
    }

    private string FormatLocal(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Zone).ToString("ddd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
        + $" {Zone.Id}";

    internal static string FormatDuration(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
        : elapsed.TotalMinutes >= 1
            ? $"{elapsed.Minutes}m"
            : $"{Math.Max(0, (int)elapsed.TotalSeconds)}s";

    /// <summary>
    /// Read a version-1 <c>schedule</c> object. Returns null when the setting is
    /// absent or explicitly null, which is how a derived config clears an inherited
    /// schedule.
    /// </summary>
    public static ClaimSchedule? FromDocument(JsonNode? node, string field = ConfigurationName)
    {
        if (node is null) return null;
        if (node is not JsonObject schedule)
        {
            throw new OptionsException($"{field} must be an object with 'timezone' and 'block'");
        }

        foreach (var (name, _) in schedule)
        {
            if (name is not ("timezone" or "block" or "minWindowRemaining"))
            {
                throw new OptionsException($"{field} has an unknown property '{name}'; use timezone, block, or minWindowRemaining");
            }
        }

        var zone = ParseZone(schedule["timezone"], field);
        if (schedule["block"] is not JsonArray block)
        {
            throw new OptionsException($"{field}.block must be an array of windows such as \"mon-fri 01:00-04:00\"");
        }

        var windows = new List<ClaimWindow>();
        for (var index = 0; index < block.Count; index++)
        {
            if (block[index] is not JsonValue value || !value.TryGetValue<string>(out var spec))
            {
                throw new OptionsException($"{field}.block[{index}] must be a string such as \"mon-fri 01:00-04:00\"");
            }

            windows.Add(ClaimWindow.Parse(spec, $"{field}.block[{index}]"));
        }

        if (windows.Count == 0)
        {
            throw new OptionsException($"{field}.block must list at least one window; remove the schedule setting instead");
        }

        return new ClaimSchedule(zone, windows, ParseMinimum(schedule["minWindowRemaining"], field));
    }

    private static TimeZoneInfo ParseZone(JsonNode? node, string field)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id))
        {
            throw new OptionsException($"{field}.timezone must name a time zone such as \"UTC\" or \"Europe/Madrid\"");
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new OptionsException($"{field}.timezone '{id}' is not a known time zone on this machine");
        }
    }

    private static TimeSpan ParseMinimum(JsonNode? node, string field)
    {
        if (node is null) return TimeSpan.Zero;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
        {
            throw new OptionsException($"{field}.minWindowRemaining must be a duration such as 30m or 2h");
        }

        var trimmed = text.Trim();
        if (trimmed.Length < 2 || !long.TryParse(trimmed[..^1], out var amount) || amount < 0)
        {
            throw new OptionsException($"{field}.minWindowRemaining must be a positive duration such as 30m or 2h");
        }

        try
        {
            return trimmed[^1] switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                _ => throw new OptionsException($"{field}.minWindowRemaining must be a positive duration such as 30m or 2h"),
            };
        }
        catch (OverflowException)
        {
            throw new OptionsException($"{field}.minWindowRemaining is too large");
        }
    }
}
