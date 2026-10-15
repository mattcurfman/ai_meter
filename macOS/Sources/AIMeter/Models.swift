import Foundation

/// One rate-limit window, such as Claude's 5-hour session or Codex's weekly limit.
struct UsageWindow: Identifiable, Sendable, Equatable {
    var id: String { title }
    let title: String
    /// Percent used, 0...100.
    let percent: Double
    let resetsAt: Date?
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
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}
