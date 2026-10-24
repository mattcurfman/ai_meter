// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

import Foundation

/// Reads Claude plan usage with the OAuth token that Claude Code stores in the login keychain.
struct ClaudeUsageClient: Sendable {
    func fetch() async throws -> ProviderUsage {
        let credentials = try await readCredentials()
        var request = URLRequest(url: URL(string: "https://api.anthropic.com/api/oauth/usage")!)
        request.setValue("Bearer \(credentials.accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
        request.timeoutInterval = 15

        let (data, response) = try await URLSession.shared.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 401 {
            throw UsageError("Sign-in expired. Run `claude` once to refresh it.")
        }
        guard status == 200 else {
            let header = (response as? HTTPURLResponse)?.value(forHTTPHeaderField: "Retry-After")
            throw UsageError("Claude returned HTTP \(status).", retryAfter: RetrySchedule.retryAfter(header))
        }

        let body = try JSONDecoder.usage.decode(Response.self, from: data)
        let windows: [UsageWindow] = [
            body.five_hour.map { UsageWindow(title: "Session (5 hr)", percent: $0.utilization, resetsAt: $0.resets_at) },
            body.seven_day.map { UsageWindow(title: "Weekly", percent: $0.utilization, resetsAt: $0.resets_at) },
            body.seven_day_opus.map { UsageWindow(title: "Weekly · Opus", percent: $0.utilization, resetsAt: $0.resets_at) },
            body.seven_day_sonnet.map { UsageWindow(title: "Weekly · Sonnet", percent: $0.utilization, resetsAt: $0.resets_at) },
        ].compactMap { $0 }
        return ProviderUsage(windows: windows, plan: credentials.subscriptionType?.capitalized)
    }

    private struct Response: Decodable {
        struct Window: Decodable {
            let utilization: Double
            let resets_at: Date?
        }
        let five_hour: Window?
        let seven_day: Window?
        let seven_day_opus: Window?
        let seven_day_sonnet: Window?
    }

    private struct Credentials: Decodable {
        struct OAuth: Decodable {
            let accessToken: String
            let subscriptionType: String?
        }
        let claudeAiOauth: OAuth
    }

    /// Uses `/usr/bin/security` because Claude Code writes the item with that tool,
    /// so reading it the same way doesn't raise a keychain access prompt.
    private func readCredentials() async throws -> Credentials.OAuth {
        let output: Data
        do {
            output = try await Shell.run("/usr/bin/security",
                                         ["find-generic-password", "-s", "Claude Code-credentials", "-w"])
        } catch {
            throw UsageError("Claude Code sign-in not found. Run `claude` and log in.")
        }
        do {
            return try JSONDecoder().decode(Credentials.self, from: output).claudeAiOauth
        } catch {
            throw UsageError("Claude Code sign-in isn't a Claude subscription login.")
        }
    }
}

enum Shell {
    static func run(_ executable: String, _ arguments: [String]) async throws -> Data {
        try await withCheckedThrowingContinuation { continuation in
            let process = Process()
            let stdout = Pipe()
            process.executableURL = URL(fileURLWithPath: executable)
            process.arguments = arguments
            process.standardOutput = stdout
            process.standardError = FileHandle.nullDevice
            process.terminationHandler = { process in
                let data = stdout.fileHandleForReading.readDataToEndOfFile()
                if process.terminationStatus == 0 {
                    continuation.resume(returning: data)
                } else {
                    continuation.resume(throwing: UsageError("\(executable) exited with \(process.terminationStatus)"))
                }
            }
            do { try process.run() } catch { continuation.resume(throwing: error) }
        }
    }
}

extension JSONDecoder {
    /// Decodes ISO 8601 dates with or without fractional seconds.
    static var usage: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let string = try decoder.singleValueContainer().decode(String.self)
            let formatter = ISO8601DateFormatter()
            formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
            if let date = formatter.date(from: string) { return date }
            formatter.formatOptions = [.withInternetDateTime]
            if let date = formatter.date(from: string) { return date }
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Bad date \(string)"))
        }
        return decoder
    }
}
