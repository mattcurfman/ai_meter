// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

import Foundation

/// One rate-limit window, such as Claude's 5-hour session or Codex's weekly limit.
struct UsageWindow: Identifiable, Sendable, Equatable {
    var id: String { title }
    let title: String
    /// Percent used, 0...100.
    let percent: Double
    let resetsAt: Date?

    init(title: String, percent: Double, resetsAt: Date?) {
        self.title = title
        self.percent = percent.isFinite ? min(100, max(0, percent)) : 0
        self.resetsAt = resetsAt
    }
}

struct ProviderUsage: Sendable, Equatable {
    let windows: [UsageWindow]
    let plan: String?

    /// The tightest window, which is what the menu bar shows.
    var peakPercent: Double? { windows.map(\.percent).max() }
}

enum Provider: String, CaseIterable, Identifiable, Sendable {
    case claude, codex
    var id: String { rawValue }

    var name: String {
        switch self {
        case .claude: "Claude"
        case .codex: "Codex"
        }
    }

    /// Single-letter prefix for the compact menu bar label.
    var initial: String {
        switch self {
        case .claude: "C"
        case .codex: "X"
        }
    }

    var dashboardURL: URL {
        switch self {
        case .claude: URL(string: "https://claude.ai/settings/usage")!
        case .codex: URL(string: "https://chatgpt.com/codex/settings/usage")!
        }
    }
}

enum ProviderState: Sendable, Equatable {
    case loading
    case loaded(ProviderUsage)
    case failed(String)

    var usage: ProviderUsage? {
        if case .loaded(let usage) = self { usage } else { nil }
    }
}

struct UsageError: LocalizedError {
    let message: String
    let retryAfter: TimeInterval
    init(_ message: String, retryAfter: TimeInterval = 0) {
        self.message = message
        self.retryAfter = retryAfter
    }
    var errorDescription: String? { message }
}

struct RetrySchedule {
    private(set) var nextAttempt: TimeInterval = 0
    private var failures = 0

    func ready(_ now: TimeInterval) -> Bool { now >= nextAttempt }

    mutating func success(_ now: TimeInterval) {
        failures = 0
        nextAttempt = now + 300
    }

    mutating func fail(_ now: TimeInterval, retryAfter: TimeInterval = 0,
                       random: Double = Double.random(in: 0..<1)) {
        failures = min(failures + 1, 5)
        let maximum = min(300 * pow(2, Double(failures)), 3600)
        nextAttempt = now + max(maximum / 2 + random * maximum / 2, retryAfter)
    }

    static func retryAfter(_ header: String?, now: Date = Date()) -> TimeInterval {
        guard let header else { return 0 }
        if let seconds = Double(header), seconds.isFinite {
            return max(0, seconds)
        }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        guard let date = formatter.date(from: header) else { return 0 }
        return max(0, date.timeIntervalSince(now))
    }
}
