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

/// Each glass shows the allowance left in the provider's tightest window.
struct MenuBarLabel: View {
    let store: UsageStore

    var body: some View {
        HStack(spacing: 6) {
            ForEach(Provider.allCases) { provider in
                Text(provider.initial)
                if let peak = store.state(for: provider).usage?.peakPercent {
                    Image(nsImage: glassImage(remaining: 100 - peak))
                    Text(percentText(for: provider))
                } else {
                    Text("–")
                }
            }
        }
        .monospacedDigit()
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibilitySummary)
    }

    private var accessibilitySummary: String {
        Provider.allCases.map {
            store.state(for: $0).usage?.peakPercent == nil
                ? "\($0.name) usage unavailable"
                : "\($0.name) \(percentText(for: $0)) remaining"
        }.joined(separator: ", ")
    }

    private func glassImage(remaining: Double) -> NSImage {
        let renderer = ImageRenderer(content: BeerGlass(remaining: remaining, monochrome: true)
            .frame(width: 12, height: 17)
            .environment(\.colorScheme, .light))
        renderer.scale = 2
        let image = renderer.nsImage ?? NSImage(size: NSSize(width: 12, height: 17))
        image.isTemplate = true
        return image
    }

    private func percentText(for provider: Provider) -> String {
        guard let peak = store.state(for: provider).usage?.peakPercent else { return "–" }
        return "\(Int((100 - peak).rounded()))%"
    }
}
