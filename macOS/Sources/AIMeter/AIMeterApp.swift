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

/// Compact status item: each provider's logo followed by its percent, e.g. "[A] 8%  [OpenAI] 0%".
/// A menu bar label shows only one image reliably, so the logos and text are drawn into one template image
/// that follows the menu bar's appearance.
struct MenuBarLabel: View {
    let store: UsageStore

    var body: some View {
        Image(nsImage: summaryImage)
            .accessibilityLabel(accessibilitySummary)
    }

    private var summaryImage: NSImage {
        let font = NSFont.menuBarFont(ofSize: 0)
        let renderer = ImageRenderer(content: HStack(spacing: 8) {
            ForEach(Provider.allCases) { provider in
                HStack(spacing: 3) {
                    Image(nsImage: NSImage(data: Data(provider.logoSVG.utf8)) ?? NSImage())
                        .resizable()
                        .renderingMode(.template)
                        .frame(width: font.pointSize - 1, height: font.pointSize - 1)
                    Text(percentText(for: provider)).monospacedDigit()
                }
            }
        }
        .font(Font(font))
        .foregroundStyle(.black))
        renderer.scale = 2
        let image = renderer.nsImage ?? NSImage()
        image.isTemplate = true
        return image
    }

    private var accessibilitySummary: String {
        Provider.allCases.map { "\($0.name) \(percentText(for: $0)) used" }.joined(separator: ", ")
    }

    private func percentText(for provider: Provider) -> String {
        guard let peak = store.state(for: provider).usage?.peakPercent else { return "–" }
        return "\(Int(peak.rounded()))%"
    }
}
