// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

import SwiftUI

@main
struct AIMeterApp: App {
    @State private var store = UsageStore()

    var body: some Scene {
        MenuBarExtra {
            UsagePanel(store: store)
        } label: {
            MenuBarLabel(store: store)
        }
        .menuBarExtraStyle(.window)
    }
}

/// Compact status item, e.g. "C 8%  X 0%". Template-rendered so it follows the menu bar's appearance.
struct MenuBarLabel: View {
    let store: UsageStore

    var body: some View {
        Text(summary)
            .monospacedDigit()
            .accessibilityLabel(accessibilitySummary)
    }

    private var summary: String {
        Provider.allCases.map { "\($0.initial) \(percentText(for: $0))" }.joined(separator: "  ")
    }

    private var accessibilitySummary: String {
        Provider.allCases.map { "\($0.name) \(percentText(for: $0)) used" }.joined(separator: ", ")
    }

    private func percentText(for provider: Provider) -> String {
        guard let peak = store.state(for: provider).usage?.peakPercent else { return "–" }
        return "\(Int(peak.rounded()))%"
    }
}
