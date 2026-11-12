using System.Text;
using AIUsageBar.Core;

namespace UsageCore.Tests;

public class RateLimitTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    public RateLimitTests() => Localization.Current = AppLanguage.Portuguese;

    [Fact]
    public void WaitStartsAtOneMinuteAndDoublesUpToTen()
    {
        var waits = Enumerable.Range(1, 6).Select(strikes => RateLimitBackoff.Wait(strikes, null).TotalSeconds);
        Assert.Equal([60, 120, 240, 480, 600, 600], waits);
        Assert.Equal(600, RateLimitBackoff.Wait(500, null).TotalSeconds);
    }

    [Fact]
    public void ServerWaitWinsWithinBounds()
    {
        Assert.Equal(90, RateLimitBackoff.Wait(3, TimeSpan.FromSeconds(90)).TotalSeconds);
        Assert.Equal(15, RateLimitBackoff.Wait(1, TimeSpan.FromSeconds(2)).TotalSeconds);
        Assert.Equal(15, RateLimitBackoff.Wait(1, TimeSpan.FromSeconds(-30)).TotalSeconds);
        Assert.Equal(3600, RateLimitBackoff.Wait(1, TimeSpan.FromDays(1)).TotalSeconds);
    }

    [Fact]
    public void RecordAndReset()
    {
        var backoff = new RateLimitBackoff();
        Assert.False(backoff.IsWaiting(Now));
        backoff.Record(null, Now);
        backoff.Record(null, Now);
        Assert.Equal(2, backoff.Strikes);
        Assert.Equal(Now.AddSeconds(120), backoff.Until);
        Assert.True(backoff.IsWaiting(Now.AddSeconds(119)));
        Assert.False(backoff.IsWaiting(Now.AddSeconds(120)));
        backoff.Reset();
        Assert.Equal(0, backoff.Strikes);
        Assert.Null(backoff.Until);
    }

    [Fact]
    public void ParsesRetryAfter()
    {
        Assert.Equal(120, RateLimitBackoff.ServerWait("120", null, null, Now)?.TotalSeconds);
        Assert.Equal(45, RateLimitBackoff.ServerWait(" 45 ", null, null, Now)?.TotalSeconds);
        // 1_800_000_000 is 2027-01-15 08:00:00 UTC.
        Assert.Equal(300, RateLimitBackoff.ServerWait("Fri, 15 Jan 2027 08:05:00 GMT", null, null, Now)?.TotalSeconds);
        Assert.Null(RateLimitBackoff.ServerWait("soon", null, null, Now));
        Assert.Null(RateLimitBackoff.ServerWait("-30", null, null, Now));
        Assert.Null(RateLimitBackoff.ServerWait(null, null, null, Now));
        Assert.Equal(3600, RateLimitBackoff.Wait(1, RateLimitBackoff.ServerWait("99999999999999999", null, null, Now)).TotalSeconds);
    }

    [Fact]
    public void GitHubResetCountsOnlyWhenTheQuotaIsUsedUp()
    {
        var reset = (Now.ToUnixTimeSeconds() + 600).ToString();
        Assert.Equal(600, RateLimitBackoff.ServerWait(null, "0", reset, Now)?.TotalSeconds);
        Assert.Null(RateLimitBackoff.ServerWait(null, "12", reset, Now));
        Assert.Equal(30, RateLimitBackoff.ServerWait("30", "0", reset, Now)?.TotalSeconds);
    }

    [Fact]
    public void MapsResponsesToErrors()
    {
        static UsageException Failure(int status, params (string Name, string Value)[] headers) =>
            Http.Failure(status, name => headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value, Now);

        var plain = Failure(429);
        Assert.Equal(UsageErrorKind.RateLimited, plain.Kind);
        Assert.Null(plain.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(90), Failure(429, ("Retry-After", "90")).RetryAfter);
        Assert.Equal(UsageErrorKind.RateLimited, Failure(403, ("x-ratelimit-remaining", "0")).Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), Failure(403, ("Retry-After", "60")).RetryAfter);
        Assert.Equal(UsageErrorKind.Unauthorized, Failure(403).Kind);
        Assert.Equal(UsageErrorKind.Unauthorized, Failure(401, ("Retry-After", "60")).Kind);
        Assert.Equal(UsageErrorKind.Http, Failure(500).Kind);
    }

    [Fact]
    public void FriendlyMessageCountsTheWaitInMinutes()
    {
        var error = new UsageException(UsageErrorKind.RateLimited, 429);
        Assert.True(error.IsRateLimit);
        Assert.False(new UsageException(UsageErrorKind.Http, 500).IsRateLimit);
        Assert.Equal(
            "Muitas atualizações seguidas. Aguarde um pouquinho: tento de novo em 1 min.",
            error.MessageFor(ProviderId.Claude, Now.AddSeconds(60), Now));
        Assert.EndsWith("em 2 min.", error.MessageFor(ProviderId.Claude, Now.AddSeconds(61), Now));
        Assert.EndsWith("em 1 min.", error.MessageFor(ProviderId.Claude, Now.AddSeconds(-5), Now));
        Assert.Equal("Muitas atualizações seguidas. Aguarde um pouquinho e tente de novo.", error.MessageFor(ProviderId.Claude));
        Localization.Current = AppLanguage.English;
        Assert.Equal(
            "Too many refreshes in a row. Hang on a moment: trying again in 4 min.",
            error.MessageFor(ProviderId.Codex, Now.AddSeconds(240), Now));
    }

    [Fact]
    public void TrayKeepsTheNumberAndSaysItIsANotice()
    {
        var notice = "Muitas atualizações seguidas.";
        var claude = ClaudeProvider.SnapshotFromResponse(Encoding.UTF8.GetBytes(ClaudeTests.Sample), "Max 5x")!;
        var segment = TraySegment.Make(ProviderId.Claude, TrayDisplay.Session, claude, notice, isNotice: true)!;
        Assert.Equal("42", segment.Number);
        Assert.Contains("ⓘ " + notice, segment.Tooltip);
        Assert.Equal("–", TraySegment.Make(ProviderId.Claude, TrayDisplay.Session, null, notice, isNotice: true)!.Number);
        Assert.Equal("!", TraySegment.Make(ProviderId.Claude, TrayDisplay.Session, null, "boom")!.Number);
    }
}
