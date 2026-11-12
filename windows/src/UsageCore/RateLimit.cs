using System.Globalization;
using static AIUsageBar.Core.Localization;

namespace AIUsageBar.Core;

/// <summary>
/// After "too many requests" (HTTP 429): how long one agent waits before it is asked again.
/// Nothing asks it during the wait (timer, popup, refresh button, resume), and the last numbers
/// stay on screen. Same rules as the Mac app.
/// </summary>
public sealed class RateLimitBackoff
{
    /// <summary>The first wait when the server does not say how long; it doubles with each 429 in a row.</summary>
    public static readonly TimeSpan FirstWait = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);
    /// <summary>The server's own wait (<c>Retry-After</c>) is kept within these bounds.</summary>
    public static readonly TimeSpan MinServerWait = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxServerWait = TimeSpan.FromHours(1);

    /// <summary>429s in a row; a successful update resets it.</summary>
    public int Strikes { get; private set; }

    /// <summary>The agent is not asked again before this moment.</summary>
    public DateTimeOffset? Until { get; private set; }

    /// <summary>1, 2, 4, 8 and then 10 minutes, or the server's wait when it sends one.</summary>
    public static TimeSpan Wait(int strikes, TimeSpan? serverWait)
    {
        if (serverWait is { } server) return server < MinServerWait ? MinServerWait : server > MaxServerWait ? MaxServerWait : server;
        var doubled = FirstWait * Math.Pow(2, Math.Clamp(strikes, 1, 16) - 1);
        return doubled > MaxWait ? MaxWait : doubled;
    }

    public void Record(TimeSpan? serverWait, DateTimeOffset now)
    {
        Strikes++;
        Until = now + Wait(Strikes, serverWait);
    }

    public void Reset()
    {
        Strikes = 0;
        Until = null;
    }

    public bool IsWaiting(DateTimeOffset now) => Until is { } until && until > now;

    /// <summary>The friendly notice shown instead of an error; <paramref name="until"/> null when nothing retries by itself (<c>--dump</c>).</summary>
    public static string Message(DateTimeOffset? until, DateTimeOffset now)
    {
        if (until is not { } at)
        {
            return T(
                "Muitas atualizações seguidas. Aguarde um pouquinho e tente de novo.",
                "Too many refreshes in a row. Wait a moment and try again.");
        }
        var minutes = Math.Max(1, (int)Math.Ceiling((at - now).TotalMinutes));
        return T(
            $"Muitas atualizações seguidas. Aguarde um pouquinho: tento de novo em {minutes} min.",
            $"Too many refreshes in a row. Hang on a moment: trying again in {minutes} min.");
    }

    /// <summary>
    /// The server's wait: <c>Retry-After</c> as seconds ("120") or as an HTTP date, or else GitHub's
    /// <c>x-ratelimit-reset</c> (Unix time) when <c>x-ratelimit-remaining</c> is 0.
    /// </summary>
    public static TimeSpan? ServerWait(string? retryAfter, string? remaining, string? reset, DateTimeOffset now)
    {
        var value = retryAfter?.Trim();
        if (!string.IsNullOrEmpty(value))
        {
            // Capped here so a huge number cannot overflow TimeSpan; Wait() bounds it again.
            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                return TimeSpan.FromSeconds(Math.Min(seconds, MaxServerWait.TotalSeconds * 2));
            }
            if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date - now;
        }
        if (remaining?.Trim() == "0"
            && long.TryParse(reset?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var resetAt)
            && resetAt < DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return DateTimeOffset.FromUnixTimeSeconds(resetAt) - now;
        }
        return null;
    }

    /// <summary>A 429, or a 403 that carries a rate limit (GitHub answers some of its limits with 403).</summary>
    public static bool IsRateLimit(int status, string? retryAfter, string? remaining) =>
        status == 429 || (status == 403 && (retryAfter is not null || remaining?.Trim() == "0"));
}
