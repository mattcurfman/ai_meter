// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

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
    private var schedules: [Provider: RetrySchedule] = [.claude: RetrySchedule(), .codex: RetrySchedule()]

    init() {
        timer = Task { [weak self] in
            while !Task.isCancelled {
                await self?.refresh()
                try? await Task.sleep(for: .seconds(60))
            }
        }
    }

    private func refresh() async {
        async let claudeResult: Void = refresh(.claude)
        async let codexResult: Void = refresh(.codex)
        _ = await (claudeResult, codexResult)
    }

    func state(for provider: Provider) -> ProviderState { states[provider] ?? .loading }

    private func refresh(_ provider: Provider) async {
        guard var schedule = schedules[provider], schedule.ready(ProcessInfo.processInfo.systemUptime) else { return }
        do {
            let usage: ProviderUsage
            switch provider {
            case .claude: usage = try await claude.fetch()
            case .codex: usage = try await codex.fetch()
            }
            guard !Task.isCancelled else { return }
            states[provider] = .loaded(usage)
            schedule.success(ProcessInfo.processInfo.systemUptime)
        } catch {
            guard !Task.isCancelled else { return }
            states[provider] = .failed(error.localizedDescription)
            schedule.fail(ProcessInfo.processInfo.systemUptime,
                          retryAfter: (error as? UsageError)?.retryAfter ?? 0)
        }
        schedules[provider] = schedule
        lastUpdated = Date()
    }
}
