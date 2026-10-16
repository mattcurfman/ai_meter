// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

import Foundation

/// Reads Codex plan usage with the ChatGPT token that the Codex CLI stores in ~/.codex/auth.json.
struct CodexUsageClient: Sendable {
    func fetch() async throws -> ProviderUsage {
        let tokens = try readTokens()
        var request = URLRequest(url: URL(string: "https://chatgpt.com/backend-api/wham/usage")!)
        request.setValue("Bearer \(tokens.access_token)", forHTTPHeaderField: "Authorization")
        if let account = tokens.account_id {
            request.setValue(account, forHTTPHeaderField: "ChatGPT-Account-Id")
        }
        request.setValue("codex_cli_rs", forHTTPHeaderField: "User-Agent")
        request.timeoutInterval = 15

        let (data, response) = try await URLSession.shared.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 401 || status == 403 {
            throw UsageError("Sign-in expired. Run `codex` once to refresh it.")
        }
        guard status == 200 else { throw UsageError("Codex returned HTTP \(status).") }

        let body = try JSONDecoder().decode(Response.self, from: data)
        let windows = [body.rate_limit?.primary_window, body.rate_limit?.secondary_window]
            .compactMap { $0 }
            .map { window in
                UsageWindow(title: Self.title(forSeconds: window.limit_window_seconds),
                            percent: window.used_percent,
                            resetsAt: window.reset_at.map { Date(timeIntervalSince1970: $0) })
            }
        return ProviderUsage(windows: windows, plan: body.plan_type?.capitalized)
    }

    static func title(forSeconds seconds: Int?) -> String {
        guard let seconds else { return "Limit" }
        switch seconds {
        case 604_800: return "Weekly"
        case 86_400: return "Daily"
        case ..<86_400: return "Session (\(seconds / 3600) hr)"
        default: return "\(seconds / 86_400)-day"
        }
    }

    private struct Response: Decodable {
        struct Window: Decodable {
            let used_percent: Double
            let limit_window_seconds: Int?
            let reset_at: Double?
        }
        struct RateLimit: Decodable {
            let primary_window: Window?
            let secondary_window: Window?
        }
        let plan_type: String?
        let rate_limit: RateLimit?
    }

    private struct Tokens: Decodable {
        let access_token: String
        let account_id: String?
    }

    private func readTokens() throws -> Tokens {
        struct Auth: Decodable { let tokens: Tokens? }
        let home = ProcessInfo.processInfo.environment["CODEX_HOME"].map { URL(fileURLWithPath: $0) }
            ?? FileManager.default.homeDirectoryForCurrentUser.appending(path: ".codex")
        guard let data = try? Data(contentsOf: home.appending(path: "auth.json")) else {
            throw UsageError("Codex sign-in not found. Run `codex login`.")
        }
        guard let tokens = try? JSONDecoder().decode(Auth.self, from: data).tokens else {
            throw UsageError("Codex is using an API key, which has no plan usage limits.")
        }
        return tokens
    }
}
