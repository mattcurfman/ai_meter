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

    /// Company logo (Anthropic or OpenAI) for the compact menu bar label, as 24×24 SVG.
    /// Path data from Simple Icons (CC0 1.0). The logos are trademarks of their owners.
    var logoSVG: String {
        let path = switch self {
        case .claude:
            "M17.3041 3.541h-3.6718l6.696 16.918H24Zm-10.6082 0L0 20.459h3.7442l1.3693-3.5527h7.0052l1.3693 3.5528h3.7442L10.5363 3.5409Zm-.3712 10.2232 2.2914-5.9456 2.2914 5.9456Z"
        case .codex:
            "M22.2819 9.8211a5.9847 5.9847 0 0 0-.5157-4.9108 6.0462 6.0462 0 0 0-6.5098-2.9A6.0651 6.0651 0 0 0 4.9807 4.1818a5.9847 5.9847 0 0 0-3.9977 2.9 6.0462 6.0462 0 0 0 .7427 7.0966 5.98 5.98 0 0 0 .511 4.9107 6.051 6.051 0 0 0 6.5146 2.9001A5.9847 5.9847 0 0 0 13.2599 24a6.0557 6.0557 0 0 0 5.7718-4.2058 5.9894 5.9894 0 0 0 3.9977-2.9001 6.0557 6.0557 0 0 0-.7475-7.0729zm-9.022 12.6081a4.4755 4.4755 0 0 1-2.8764-1.0408l.1419-.0804 4.7783-2.7582a.7948.7948 0 0 0 .3927-.6813v-6.7369l2.02 1.1686a.071.071 0 0 1 .038.052v5.5826a4.504 4.504 0 0 1-4.4945 4.4944zm-9.6607-4.1254a4.4708 4.4708 0 0 1-.5346-3.0137l.142.0852 4.783 2.7582a.7712.7712 0 0 0 .7806 0l5.8428-3.3685v2.3324a.0804.0804 0 0 1-.0332.0615L9.74 19.9502a4.4992 4.4992 0 0 1-6.1408-1.6464zM2.3408 7.8956a4.485 4.485 0 0 1 2.3655-1.9728V11.6a.7664.7664 0 0 0 .3879.6765l5.8144 3.3543-2.0201 1.1685a.0757.0757 0 0 1-.071 0l-4.8303-2.7865A4.504 4.504 0 0 1 2.3408 7.872zm16.5963 3.8558L13.1038 8.364 15.1192 7.2a.0757.0757 0 0 1 .071 0l4.8303 2.7913a4.4944 4.4944 0 0 1-.6765 8.1042v-5.6772a.79.79 0 0 0-.407-.667zm2.0107-3.0231l-.142-.0852-4.7735-2.7818a.7759.7759 0 0 0-.7854 0L9.409 9.2297V6.8974a.0662.0662 0 0 1 .0284-.0615l4.8303-2.7866a4.4992 4.4992 0 0 1 6.6802 4.66zM8.3065 12.863l-2.02-1.1638a.0804.0804 0 0 1-.038-.0567V6.0742a4.4992 4.4992 0 0 1 7.3757-3.4537l-.142.0805L8.704 5.459a.7948.7948 0 0 0-.3927.6813zm1.0976-2.3654l2.602-1.4998 2.6069 1.4998v2.9994l-2.5974 1.4997-2.6067-1.4997Z"
        }
        return #"<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="\#(path)"/></svg>"#
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
