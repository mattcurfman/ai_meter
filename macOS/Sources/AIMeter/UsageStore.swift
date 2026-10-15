import Foundation
import Observation

@MainActor
@Observable
final class UsageStore {
    private(set) var states: [Provider: ProviderState] = [.claude: .loading, .codex: .loading]
    private(set) var lastUpdated: Date?

    private let claude = ClaudeUsageClient()
    private let codex = CodexUsageClient()
    private var timer: Task<Void, Never>?

    /// The only refresh trigger; both endpoints are informal, so poll gently.
    static let refreshInterval: Duration = .seconds(300)

    init() {
        timer = Task { [weak self] in
            while !Task.isCancelled {
                await self?.refresh()
                try? await Task.sleep(for: Self.refreshInterval)
            }
        }
    }

    private func refresh() async {
        async let claudeResult = Self.capture { try await self.claude.fetch() }
        async let codexResult = Self.capture { try await self.codex.fetch() }
        let (claudeState, codexState) = await (claudeResult, codexResult)
        states[.claude] = merged(claudeState, previous: states[.claude])
        states[.codex] = merged(codexState, previous: states[.codex])
        lastUpdated = Date()
    }

    func state(for provider: Provider) -> ProviderState { states[provider] ?? .loading }

    private nonisolated static func capture(_ work: @Sendable () async throws -> ProviderUsage) async -> ProviderState {
        do { return .loaded(try await work()) } catch { return .failed(error.localizedDescription) }
    }

    /// A transient network failure shouldn't wipe out the last good numbers.
    private func merged(_ new: ProviderState, previous: ProviderState?) -> ProviderState {
        if case .failed = new, let previous, previous.usage != nil,
           let lastUpdated, Date().timeIntervalSince(lastUpdated) < 3600 {
            return previous
        }
        return new
    }
}
