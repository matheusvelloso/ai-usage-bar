import XCTest
@testable import AIUsageBar
@testable import UsageCore

/// A provider that plays back a list of answers and counts how often it is asked.
private final class ScriptedProvider: UsageProvider, @unchecked Sendable {
    let id: ProviderID
    private let lock = NSLock()
    private var answers: [UsageError?]
    private var count = 0

    /// `nil` in `answers` is a successful update; the last answer repeats.
    init(id: ProviderID, answers: [UsageError?]) {
        self.id = id
        self.answers = answers
    }

    var calls: Int { lock.withLock { count } }

    func isAvailable() -> Bool { true }

    func fetch() async throws -> ProviderFetchResult {
        let answer: UsageError? = lock.withLock {
            count += 1
            return answers.count > 1 ? answers.removeFirst() : answers.first ?? nil
        }
        if let answer { throw answer }
        let item = LimitItem(id: "session", kind: "session", percent: 40, severity: .normal, resetsAt: nil, group: .session)
        return ProviderFetchResult(snapshot: ProviderSnapshot(provider: id, planName: "Max", items: [item]), cache: Data())
    }

    func snapshot(fromCache data: Data) -> ProviderSnapshot? { nil }
}

@MainActor
final class RateLimitRefreshTests: XCTestCase {
    private var defaults: UserDefaults!
    private var suite: String!
    private var time = Date(timeIntervalSince1970: 1_800_000_000)

    override func setUp() async throws {
        suite = "RateLimitRefreshTests-\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suite)
        defaults.set("pt", forKey: "language")
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suite)
    }

    private func makeStore(_ providers: [ScriptedProvider]) -> UsageStore {
        let store = UsageStore(providers: providers, defaults: defaults)
        store.clock = { [unowned self] in self.time }
        return store
    }

    private func wait(_ seconds: TimeInterval) { time += seconds }

    private let tooMany = UsageError.rateLimited(retryAfter: nil)

    func testKeepsTheNumbersAndWaitsAMinute() async {
        let claude = ScriptedProvider(id: .claude, answers: [nil, tooMany, nil])
        let store = makeStore([claude])
        await store.refresh()
        wait(180)
        await store.refresh()

        let state = store.state(for: .claude)
        XCTAssertTrue(state.isRateLimited)
        XCTAssertFalse(state.hasWarning, "a rate limit is a notice, not an error")
        XCTAssertFalse(store.hasError)
        XCTAssertEqual(state.snapshot?.items.first?.percent, 40, "the last numbers stay on screen")
        XCTAssertEqual(store.menuBarSegments.first?.lines, ["40%"])
        XCTAssertEqual(state.message(for: .claude, now: time), "Muitas atualizações seguidas. Aguarde um pouquinho: tento de novo em 1 min.")

        // Clicking refresh, opening the popover or waking the Mac during the wait does not ask the server.
        wait(30)
        await store.refresh()
        await store.refreshIfStale()
        XCTAssertEqual(claude.calls, 2)

        wait(31)
        await store.refresh()
        XCTAssertEqual(claude.calls, 3)
        XCTAssertNil(store.state(for: .claude).error)
    }

    func testRepeatedLimitsWaitLongerUntilAnUpdateWorks() async {
        let claude = ScriptedProvider(id: .claude, answers: [tooMany, tooMany, tooMany, nil, tooMany])
        let store = makeStore([claude])
        var waits: [TimeInterval] = []
        for _ in 0..<3 {
            await store.refresh()
            let until = store.state(for: .claude).backoff.until ?? time
            waits.append(until.timeIntervalSince(time))
            time = until
        }
        XCTAssertEqual(waits, [60, 120, 240])

        await store.refresh()
        XCTAssertEqual(store.state(for: .claude).backoff, RateLimitBackoff(), "a successful update starts over")
        wait(180)
        await store.refresh()
        XCTAssertEqual(store.state(for: .claude).backoff.until, time.addingTimeInterval(60))
    }

    func testRespectsRetryAfter() async {
        let claude = ScriptedProvider(id: .claude, answers: [.rateLimited(retryAfter: 300)])
        let store = makeStore([claude])
        await store.refresh()
        XCTAssertEqual(store.state(for: .claude).backoff.until, time.addingTimeInterval(300))
        XCTAssertEqual(store.state(for: .claude).message(for: .claude, now: time)?.suffix(9), "em 5 min.")
    }

    func testOtherAgentsKeepRefreshingDuringTheWait() async {
        let claude = ScriptedProvider(id: .claude, answers: [tooMany])
        let codex = ScriptedProvider(id: .codex, answers: [nil])
        let store = makeStore([claude, codex])
        await store.refresh()
        wait(10)
        await store.refresh()
        XCTAssertEqual(claude.calls, 1)
        XCTAssertEqual(codex.calls, 2)
    }

    func testOpeningThePopoverAsksOnlyAgentsNotAskedInTheLastMinute() async {
        let claude = ScriptedProvider(id: .claude, answers: [nil])
        let codex = ScriptedProvider(id: .codex, answers: [.network("offline")])
        let store = makeStore([claude, codex])
        await store.refresh()
        wait(30)
        await store.refreshIfStale()
        XCTAssertEqual([claude.calls, codex.calls], [1, 1], "an error alone no longer forces every agent")
        wait(30)
        await store.refreshIfStale()
        XCTAssertEqual([claude.calls, codex.calls], [2, 2])
    }
}
