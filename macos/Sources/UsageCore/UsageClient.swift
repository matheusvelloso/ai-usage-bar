import Foundation

public enum UsageError: Error, Equatable, Sendable {
    case notLoggedIn
    case unauthorized(expired: Bool)
    case keychainDenied
    /// The login is stored but could not be read just now (Cursor's database was busy).
    case loginUnreadable
    /// Too many requests (HTTP 429). `retryAfter`: the server's own wait, in seconds, when it sent one.
    case rateLimited(retryAfter: TimeInterval?)
    case http(Int)
    case network(String)
    case decoding

    /// A friendly notice rather than an error: the last numbers stay and the app asks again by itself.
    public var isRateLimit: Bool {
        if case .rateLimited = self { return true }
        return false
    }

    /// `retryAt`: when a rate-limited agent is asked again (`RateLimitBackoff.until`).
    public func message(for provider: ProviderID, retryAt: Date? = nil, now: Date = Date()) -> String {
        let app = provider.loginSource
        switch self {
        case .notLoggedIn:
            switch provider {
            case .claude:
                return tr(
                    "Não achei o login do Claude Code neste Mac. Entre com o comando claude no terminal ou pelo app do Claude.",
                    "No Claude Code login found on this Mac. Sign in with the claude command in Terminal or in the Claude app."
                )
            case .codex:
                return tr(
                    "Não achei o login do Codex. Entre com o comando codex login no terminal ou na extensão Codex do VS Code.",
                    "No Codex login found. Sign in with codex login in a terminal, or in the Codex extension for VS Code."
                )
            case .gemini:
                return tr(
                    "Não achei o login do Gemini CLI. Rode gemini no terminal e entre com o Google.",
                    "No Gemini CLI login found. Run gemini in a terminal and sign in with Google."
                )
            case .copilot:
                return tr(
                    "Não achei o login do GitHub Copilot. Entre no Copilot no VS Code ou rode gh auth login.",
                    "No GitHub Copilot login found. Sign in to Copilot in VS Code, or run gh auth login."
                )
            case .cursor:
                return tr(
                    "Não achei o login do Cursor. Abra o Cursor e entre na sua conta.",
                    "No Cursor login found. Open Cursor and sign in."
                )
            case .grokBot:
                return tr(
                    "Não achei o login do Grok Bot. Abra o Grok Bot e entre na sua conta.",
                    "No Grok Bot login found. Open Grok Bot and sign in."
                )
            }
        case .unauthorized(expired: true):
            return tr(
                "O login do \(app) expirou. Abra o \(app) uma vez para renová-lo e o medidor volta sozinho.",
                "The \(app) login expired. Open \(app) once to renew it and the meter picks up again."
            )
        case .unauthorized(expired: false):
            return tr(
                "O servidor recusou o login atual. Saia e entre de novo no \(app).",
                "The server rejected the current login. Sign out and back in to \(app)."
            )
        case .keychainDenied:
            return tr(
                "O macOS não liberou a chave do \(app) no Keychain. Clique em atualizar e escolha “Permitir sempre”.",
                "macOS did not release the \(app) key from the Keychain. Click refresh and choose “Always Allow”."
            )
        case .loginUnreadable:
            return tr(
                "Não consegui ler o login do \(app) agora. Tento de novo em breve.",
                "Could not read the \(app) login right now. Trying again soon."
            )
        case .rateLimited:
            return RateLimitBackoff.message(until: retryAt, now: now)
        case .http(let code):
            return tr("O servidor respondeu com erro \(code). Tento de novo em breve.", "The server returned error \(code). Trying again soon.")
        case .network:
            return tr("Sem conexão. Tento de novo em breve.", "No connection. Trying again soon.")
        case .decoding:
            return tr(
                "O formato dos dados de uso mudou e o medidor precisa ser atualizado.",
                "The usage data format changed and the meter needs an update."
            )
        }
    }
}

enum HTTP {
    /// Sends the request and maps HTTP failures to `UsageError`.
    static func send(_ request: URLRequest) async throws -> Data {
        let result: (Data, URLResponse)
        do {
            result = try await URLSession.shared.data(for: request)
        } catch {
            throw UsageError.network(error.localizedDescription)
        }
        let (data, response) = result
        let http = response as? HTTPURLResponse
        let status = http?.statusCode ?? -1
        if (200..<300).contains(status) { return data }
        throw failure(status: status, header: { http?.value(forHTTPHeaderField: $0) })
    }

    /// The error for a failed response; `header` looks a response header up by name.
    static func failure(status: Int, header: (String) -> String?, now: Date = Date()) -> UsageError {
        let retryAfter = header("Retry-After")
        let remaining = header("x-ratelimit-remaining")
        if RateLimitBackoff.isRateLimit(status: status, retryAfter: retryAfter, remaining: remaining) {
            let wait = RateLimitBackoff.serverWait(retryAfter: retryAfter, remaining: remaining, reset: header("x-ratelimit-reset"), now: now)
            return .rateLimited(retryAfter: wait)
        }
        switch status {
        case 401, 403: return .unauthorized(expired: false)
        default: return .http(status)
        }
    }
}

/// Calls the endpoint Claude Code's `/usage` uses. Undocumented, so it may
/// change; see docs/usage-api.md at the repository root.
public struct UsageClient: Sendable {
    public static let endpoint = URL(string: "https://api.anthropic.com/api/oauth/usage")!
    public static let betaHeader = "oauth-2025-04-20"

    public init() {}

    /// Returns the raw JSON so the caller can both decode and cache it.
    public func fetchUsage(accessToken: String) async throws -> Data {
        var request = URLRequest(url: Self.endpoint, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue(Self.betaHeader, forHTTPHeaderField: "anthropic-beta")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("ai-usage-bar", forHTTPHeaderField: "User-Agent")
        return try await HTTP.send(request)
    }
}
