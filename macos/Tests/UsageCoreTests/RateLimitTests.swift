import XCTest
@testable import UsageCore

final class RateLimitTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    override func setUp() {
        super.setUp()
        AppLanguage.current = .portuguese
    }

    func testWaitStartsAtOneMinuteAndDoublesUpToTen() {
        let waits = (1...6).map { RateLimitBackoff.wait(strikes: $0, serverWait: nil) }
        XCTAssertEqual(waits, [60, 120, 240, 480, 600, 600])
        XCTAssertEqual(RateLimitBackoff.wait(strikes: 500, serverWait: nil), 600)
    }

    func testServerWaitWinsWithinBounds() {
        XCTAssertEqual(RateLimitBackoff.wait(strikes: 3, serverWait: 90), 90)
        XCTAssertEqual(RateLimitBackoff.wait(strikes: 1, serverWait: 2), 15, "a tiny Retry-After does not make it hammer the server")
        XCTAssertEqual(RateLimitBackoff.wait(strikes: 1, serverWait: -30), 15, "a date in the past")
        XCTAssertEqual(RateLimitBackoff.wait(strikes: 1, serverWait: 86_400), 3600)
    }

    func testRecordAndReset() {
        var backoff = RateLimitBackoff()
        XCTAssertFalse(backoff.isWaiting(now: now))
        backoff.record(serverWait: nil, now: now)
        backoff.record(serverWait: nil, now: now)
        XCTAssertEqual(backoff.strikes, 2)
        XCTAssertEqual(backoff.until, now.addingTimeInterval(120))
        XCTAssertTrue(backoff.isWaiting(now: now.addingTimeInterval(119)))
        XCTAssertFalse(backoff.isWaiting(now: now.addingTimeInterval(120)))
        backoff.reset()
        XCTAssertEqual(backoff, RateLimitBackoff())
    }

    func testParsesRetryAfter() {
        XCTAssertEqual(RateLimitBackoff.serverWait(retryAfter: "120", remaining: nil, reset: nil, now: now), 120)
        XCTAssertEqual(RateLimitBackoff.serverWait(retryAfter: " 45 ", remaining: nil, reset: nil, now: now), 45)
        // 2027-01-15 08:00:00 UTC is 1_800_000_000 + 300 s.
        XCTAssertEqual(RateLimitBackoff.serverWait(retryAfter: "Fri, 15 Jan 2027 08:05:00 GMT", remaining: nil, reset: nil, now: now), 300)
        XCTAssertNil(RateLimitBackoff.serverWait(retryAfter: "soon", remaining: nil, reset: nil, now: now))
        XCTAssertNil(RateLimitBackoff.serverWait(retryAfter: nil, remaining: nil, reset: nil, now: now))
    }

    func testGitHubResetCountsOnlyWhenTheQuotaIsUsedUp() {
        let reset = String(Int(now.timeIntervalSince1970) + 600)
        XCTAssertEqual(RateLimitBackoff.serverWait(retryAfter: nil, remaining: "0", reset: reset, now: now), 600)
        XCTAssertNil(RateLimitBackoff.serverWait(retryAfter: nil, remaining: "12", reset: reset, now: now))
        XCTAssertEqual(RateLimitBackoff.serverWait(retryAfter: "30", remaining: "0", reset: reset, now: now), 30, "Retry-After comes first")
    }

    func testMapsResponsesToErrors() {
        func failure(_ status: Int, _ headers: [String: String] = [:]) -> UsageError {
            HTTP.failure(status: status, header: { headers[$0] }, now: now)
        }
        XCTAssertEqual(failure(429), .rateLimited(retryAfter: nil))
        XCTAssertEqual(failure(429, ["Retry-After": "90"]), .rateLimited(retryAfter: 90))
        XCTAssertEqual(failure(403, ["x-ratelimit-remaining": "0"]), .rateLimited(retryAfter: nil), "GitHub's 403 rate limit")
        XCTAssertEqual(failure(403, ["Retry-After": "60"]), .rateLimited(retryAfter: 60))
        XCTAssertEqual(failure(403), .unauthorized(expired: false))
        XCTAssertEqual(failure(401, ["Retry-After": "60"]), .unauthorized(expired: false))
        XCTAssertEqual(failure(500), .http(500))
    }

    func testFriendlyMessageCountsTheWaitInMinutes() {
        let error = UsageError.rateLimited(retryAfter: nil)
        XCTAssertTrue(error.isRateLimit)
        XCTAssertFalse(UsageError.http(500).isRateLimit)
        XCTAssertEqual(
            error.message(for: .claude, retryAt: now.addingTimeInterval(60), now: now),
            "Muitas atualizações seguidas. Aguarde um pouquinho: tento de novo em 1 min."
        )
        XCTAssertEqual(error.message(for: .claude, retryAt: now.addingTimeInterval(61), now: now).suffix(9), "em 2 min.")
        XCTAssertEqual(error.message(for: .claude, retryAt: now.addingTimeInterval(-5), now: now).suffix(9), "em 1 min.")
        XCTAssertEqual(error.message(for: .claude), "Muitas atualizações seguidas. Aguarde um pouquinho e tente de novo.")
        AppLanguage.current = .english
        XCTAssertEqual(
            error.message(for: .codex, retryAt: now.addingTimeInterval(240), now: now),
            "Too many refreshes in a row. Hang on a moment: trying again in 4 min."
        )
    }
}
