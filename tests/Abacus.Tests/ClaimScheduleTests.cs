using System.Text.Json.Nodes;
using Abacus;

namespace Abacus.Tests;

public sealed class ClaimScheduleTests
{
    // 2026-09-14 is a Monday, so weekday assertions read directly.
    private static DateTimeOffset Utc(int day, int hour, int minute) =>
        new(2026, 9, day, hour, minute, 0, TimeSpan.Zero);

    private static ClaimSchedule Parse(string json) =>
        ClaimSchedule.FromDocument(JsonNode.Parse(json), "schedule")!;

    private static ClaimSchedule Peak() => Parse("""
        {"timezone":"UTC","block":["mon-fri 01:00-04:00"]}
        """);

    [Fact]
    public void BlocksExactlyTheConfiguredHours()
    {
        var schedule = Peak();

        Assert.True(schedule.IsBlockedAt(Utc(14, 1, 0)));
        Assert.True(schedule.IsBlockedAt(Utc(14, 3, 59)));
        Assert.False(schedule.IsBlockedAt(Utc(14, 0, 59)));
        Assert.False(schedule.IsBlockedAt(Utc(14, 4, 0)));
        Assert.True(schedule.IsBlockedAt(Utc(15, 2, 0)));
        Assert.False(schedule.IsBlockedAt(Utc(19, 2, 0)));
        Assert.False(schedule.IsBlockedAt(Utc(20, 2, 0)));
    }

    [Fact]
    public void ClaimsStayOpenOutsideEveryWindow()
    {
        var schedule = Peak();

        Assert.True(schedule.CanClaimAt(Utc(14, 12, 0), out var reason));
        Assert.Empty(reason);
        Assert.False(schedule.CanClaimAt(Utc(14, 2, 0), out reason));
        Assert.Contains("blocked", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowCrossingMidnightCoversBothSidesOfTheBoundary()
    {
        var schedule = Parse("""
            {"timezone":"UTC","block":["fri 22:00-02:00"]}
            """);

        Assert.False(schedule.IsBlockedAt(Utc(18, 21, 59)));
        Assert.True(schedule.IsBlockedAt(Utc(18, 22, 0)));
        Assert.True(schedule.IsBlockedAt(Utc(18, 23, 59)));
        Assert.True(schedule.IsBlockedAt(Utc(19, 0, 0)));
        Assert.True(schedule.IsBlockedAt(Utc(19, 1, 59)));
        Assert.False(schedule.IsBlockedAt(Utc(19, 2, 0)));
    }

    [Fact]
    public void ReadsDayListsRangesAndWildcards()
    {
        var weekend = Parse("""
            {"timezone":"UTC","block":["sat,sun 00:00-23:59"]}
            """);
        Assert.True(weekend.IsBlockedAt(Utc(19, 12, 0)));
        Assert.False(weekend.IsBlockedAt(Utc(18, 12, 0)));

        var everyDay = Parse("""
            {"timezone":"UTC","block":["daily 12:00-13:00"]}
            """);
        Assert.True(everyDay.IsBlockedAt(Utc(20, 12, 30)));

        var overnightShift = Parse("""
            {"timezone":"UTC","block":["fri-mon 23:00-01:00"]}
            """);
        Assert.True(overnightShift.IsBlockedAt(Utc(19, 23, 30)));
        Assert.True(overnightShift.IsBlockedAt(Utc(20, 0, 30)));
        Assert.True(overnightShift.IsBlockedAt(Utc(21, 0, 30)));
        Assert.False(overnightShift.IsBlockedAt(Utc(23, 0, 30)));
    }

    [Theory]
    [InlineData("mon-fri")]
    [InlineData("mon-fri 01:00")]
    [InlineData("mon-fri 01:00-")]
    [InlineData("funday 01:00-04:00")]
    [InlineData("mon-fri 25:00-04:00")]
    [InlineData("mon-fri 01:60-04:00")]
    [InlineData("mon-fri 03:00-03:00")]
    [InlineData("")]
    public void RejectsMalformedWindowSpecs(string spec)
    {
        var exception = Assert.Throws<OptionsException>(() => ClaimWindow.Parse(spec, "schedule.block[0]"));
        Assert.Contains("schedule.block[0]", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"block":["mon-fri 01:00-04:00"]}""")]
    [InlineData("""{"timezone":"UTC"}""")]
    [InlineData("""{"timezone":"UTC","block":[]}""")]
    [InlineData("""{"timezone":"UTC","block":"mon-fri 01:00-04:00"}""")]
    [InlineData("""{"timezone":"Mars/Olympus","block":["mon-fri 01:00-04:00"]}""")]
    [InlineData("""{"timezone":"UTC","block":["mon-fri 01:00-04:00"],"allow":["sat"]}""")]
    [InlineData("""{"timezone":"UTC","block":["mon-fri 01:00-04:00"],"minWindowRemaining":"soon"}""")]
    public void RejectsIncompleteOrUnknownScheduleSettings(string json)
    {
        var exception = Assert.Throws<OptionsException>(() => Parse(json));
        Assert.Contains("schedule", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsentScheduleMeansClaimsAreNeverBlocked()
    {
        Assert.Null(ClaimSchedule.FromDocument(null));
        Assert.Null(ClaimSchedule.FromDocument(JsonNode.Parse("null")));
    }

    [Fact]
    public void MinimumRemainingTimeRefusesToStartATicketNearTheEndOfAWindow()
    {
        var schedule = Parse("""
            {"timezone":"UTC","block":["mon-fri 01:00-04:00"],"minWindowRemaining":"1h"}
            """);

        // Monday 00:30 is outside the blocked hours, but only 30 minutes remain
        // before they start, so the ticket would run straight into peak pricing.
        Assert.True(schedule.CanClaimAt(Utc(14, 12, 0), out _));
        Assert.False(schedule.CanClaimAt(Utc(14, 0, 30), out var reason));
        Assert.Contains("minWindowRemaining", reason, StringComparison.Ordinal);
        Assert.Equal(Utc(14, 4, 0), schedule.NextClaimableAt(Utc(14, 0, 30)));
    }

    [Fact]
    public void MinimumRemainingTimeIsIgnoredWhenNoWindowIsScheduled()
    {
        var schedule = Parse("""
            {"timezone":"UTC","block":["mon 12:00-13:00"],"minWindowRemaining":"45m"}
            """);

        Assert.True(schedule.CanClaimAt(Utc(14, 20, 0), out _));
        Assert.Equal(TimeSpan.FromMinutes(45), schedule.MinWindowRemaining);
    }

    [Fact]
    public void ReportsTheNextOpeningWhileBlocked()
    {
        var schedule = Peak();
        var now = Utc(14, 2, 0);

        Assert.Equal(Utc(14, 4, 0), schedule.NextClaimableAt(now));
        var description = schedule.DescribeAt(now);
        Assert.Contains("claims resume", description, StringComparison.Ordinal);
        Assert.Contains("UTC", description, StringComparison.Ordinal);
        Assert.Contains("2h", description, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribesAnOpenWindowAndItsNextBlockedHours()
    {
        var description = Peak().DescribeAt(Utc(14, 12, 0));

        Assert.Contains("claims are open", description, StringComparison.Ordinal);
        Assert.Contains("13h", description, StringComparison.Ordinal);
    }

    [Fact]
    public void WarnsWhenNoWindowCanSatisfyTheMinimumRemainingTime()
    {
        var impossible = Parse("""
            {"timezone":"UTC","block":["daily 00:00-23:59"],"minWindowRemaining":"5m"}
            """);
        Assert.Contains("can never start", Assert.Single(impossible.Warnings()), StringComparison.Ordinal);

        // The same shape is fine as long as a claim can fit inside the open stretch.
        Assert.Empty(Parse("""
            {"timezone":"UTC","block":["daily 00:00-23:59"],"minWindowRemaining":"1m"}
            """).Warnings());
        Assert.Empty(Parse("""
            {"timezone":"UTC","block":["mon-fri 01:00-04:00"],"minWindowRemaining":"6h"}
            """).Warnings());
    }

    [Fact]
    public void NextClaimableIsAlwaysAClaimableInstantDuringDaylightSavingChanges()
    {
        if (OperatingSystem.IsWindows()) return;

        // Europe/Madrid moves to summer time on 2026-03-29 and back on 2026-10-25.
        var schedule = Parse("""
            {"timezone":"Europe/Madrid","block":["sat,sun 00:00-23:59","daily 02:00-03:00"],"minWindowRemaining":"30m"}
            """);

        foreach (var start in new[] { new DateTimeOffset(2026, 3, 26, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero) })
        {
            for (var hours = 0; hours < 24 * 5; hours++)
            {
                var now = start.AddHours(hours);
                if (schedule.CanClaimAt(now, out _)) continue;
                var next = schedule.NextClaimableAt(now);
                Assert.NotNull(next);
                Assert.True(next > now, $"{next:O} must follow {now:O}");
                Assert.True(schedule.CanClaimAt(next!.Value, out _), $"{next:O} must accept a claim");
            }
        }
    }

    [Fact]
    public void WindowsUseTheConfiguredZoneNotUtc()
    {
        var schedule = Parse("""
            {"timezone":"Europe/Madrid","block":["mon-fri 01:00-04:00"]}
            """);

        // 2026-09-14 01:30 UTC is 03:30 in Madrid, inside the window.
        Assert.True(schedule.IsBlockedAt(Utc(14, 1, 30)));
        // ...and 23:30 UTC is 01:30 in Madrid, which UTC hours would have missed.
        Assert.True(schedule.IsBlockedAt(Utc(14, 23, 30)));
        Assert.False(schedule.IsBlockedAt(Utc(15, 6, 0)));
    }
}
