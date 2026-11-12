import Foundation

/// After "too many requests" (HTTP 429): how long one agent waits before it is asked again.
/// Nothing asks it during the wait (timer, popover, refresh button, wake), and the last
/// numbers stay on screen. Same rules as the Windows and Linux apps.
public struct RateLimitBackoff: Equatable, Sendable {
    /// The first wait when the server does not say how long; it doubles with each 429 in a row.
    public static let firstWait: TimeInterval = 60
    public static let maxWait: TimeInterval = 600
    /// The server's own wait (`Retry-After`) is kept within these bounds.
    public static let minServerWait: TimeInterval = 15
    public static let maxServerWait: TimeInterval = 3600

    /// 429s in a row; a successful update resets it.
    public private(set) var strikes = 0
    /// The agent is not asked again before this moment.
    public private(set) var until: Date?

    public init() {}

    /// 1, 2, 4, 8 and then 10 minutes, or the server's wait when it sends one.
    public static func wait(strikes: Int, serverWait: TimeInterval?) -> TimeInterval {
        if let serverWait { return min(max(serverWait, minServerWait), maxServerWait) }
        let doublings = Double(min(max(strikes, 1), 16) - 1)
        return min(firstWait * pow(2, doublings), maxWait)
    }

    public mutating func record(serverWait: TimeInterval?, now: Date = Date()) {
        strikes += 1
        until = now.addingTimeInterval(Self.wait(strikes: strikes, serverWait: serverWait))
    }

    public mutating func reset() { self = RateLimitBackoff() }

    public func isWaiting(now: Date = Date()) -> Bool { until.map { $0 > now } ?? false }

    /// The friendly notice shown instead of an error; `until` nil when nothing retries by itself (`--dump`).
    public static func message(until: Date?, now: Date = Date()) -> String {
        guard let until else {
            return tr(
                "Muitas atualizações seguidas. Aguarde um pouquinho e tente de novo.",
                "Too many refreshes in a row. Wait a moment and try again."
            )
        }
        let minutes = max(1, Int((until.timeIntervalSince(now) / 60).rounded(.up)))
        return tr(
            "Muitas atualizações seguidas. Aguarde um pouquinho: tento de novo em \(minutes) min.",
            "Too many refreshes in a row. Hang on a moment: trying again in \(minutes) min."
        )
    }

    /// The server's wait, in seconds: `Retry-After` as seconds ("120") or as an HTTP date, or else
    /// GitHub's `x-ratelimit-reset` (Unix time) when `x-ratelimit-remaining` is 0.
    public static func serverWait(retryAfter: String?, remaining: String?, reset: String?, now: Date = Date()) -> TimeInterval? {
        if let value = retryAfter?.trimmingCharacters(in: .whitespaces), !value.isEmpty {
            if let seconds = Int(value), seconds >= 0 { return TimeInterval(seconds) }
            if let date = httpDate.date(from: value) { return date.timeIntervalSince(now) }
        }
        if remaining?.trimmingCharacters(in: .whitespaces) == "0",
           let reset = reset.flatMap({ Int($0.trimmingCharacters(in: .whitespaces)) }) {
            return Date(timeIntervalSince1970: TimeInterval(reset)).timeIntervalSince(now)
        }
        return nil
    }

    /// A 429, or a 403 that carries a rate limit (GitHub answers some of its limits with 403).
    public static func isRateLimit(status: Int, retryAfter: String?, remaining: String?) -> Bool {
        status == 429 || (status == 403 && (retryAfter != nil || remaining?.trimmingCharacters(in: .whitespaces) == "0"))
    }

    /// "Wed, 21 Oct 2015 07:28:00 GMT".
    private static let httpDate: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(identifier: "GMT")
        formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        return formatter
    }()
}
