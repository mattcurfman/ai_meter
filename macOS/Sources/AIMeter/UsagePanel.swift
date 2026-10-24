// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

import ServiceManagement
import SwiftUI

struct UsagePanel: View {
    let store: UsageStore

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(Provider.allCases) { provider in
                ProviderSection(provider: provider, state: store.state(for: provider))
                    .padding(.horizontal, 14)
                    .padding(.vertical, 12)
                if provider != Provider.allCases.last { Divider() }
            }
            Divider()
            Footer(store: store)
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
        }
        .frame(width: 300)
    }
}

private struct ProviderSection: View {
    let provider: Provider
    let state: ProviderState

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline) {
                Text(provider.name).font(.headline)
                if let plan = state.usage?.plan {
                    Text(plan)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 6)
                        .padding(.vertical, 1)
                        .background(.quaternary, in: Capsule())
                }
                Spacer()
                Link(destination: provider.dashboardURL) {
                    Image(systemName: "arrow.up.right.square")
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
                .help("Open \(provider.name) usage page")
            }

            switch state {
            case .loading:
                ProgressView().controlSize(.small)
            case .failed(let message):
                Label(message, systemImage: "exclamationmark.triangle")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            case .loaded(let usage) where usage.windows.isEmpty:
                Text("No usage limits reported.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            case .loaded(let usage):
                ForEach(usage.windows) { WindowRow(window: $0) }
            }
        }
    }
}

private struct WindowRow: View {
    let window: UsageWindow

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .firstTextBaseline) {
                Text(window.title)
                Spacer()
                Text(window.percent / 100, format: .percent.precision(.fractionLength(0)))
                    .monospacedDigit()
                    .fontWeight(.medium)
            }
            .font(.callout)

            MeterBar(fraction: window.percent / 100, tint: tint)

            if let resetsAt = window.resetsAt {
                Text("Resets \(resetsAt, format: .relative(presentation: .named))")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .help(resetsAt.formatted(date: .abbreviated, time: .shortened))
            }
        }
        .accessibilityElement(children: .combine)
    }

    private var tint: Color {
        switch window.percent {
        case 90...: .red
        case 70..<90: .orange
        default: .accentColor
        }
    }
}

/// A capsule meter, drawn in SwiftUI rather than with `ProgressView` so the orange and
/// red warning colors don't depend on how AppKit's bar treats `.tint`.
private struct MeterBar: View {
    let fraction: Double
    let tint: Color

    var body: some View {
        GeometryReader { proxy in
            Capsule()
                .fill(.quaternary)
                .overlay(alignment: .leading) {
                    Capsule()
                        .fill(tint)
                        .frame(width: max(proxy.size.width * min(max(fraction, 0), 1), fraction > 0 ? 6 : 0))
                }
        }
        .frame(height: 6)
        .accessibilityHidden(true)
    }
}

private struct Footer: View {
    let store: UsageStore
    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled

    var body: some View {
        HStack {
            if let lastUpdated = store.lastUpdated {
                TimelineView(.periodic(from: .now, by: 30)) { _ in
                    Text("Checked \(lastUpdated, format: .relative(presentation: .named))")
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }
            Spacer()
            Menu {
                Toggle("Open at Login", isOn: $launchAtLogin)
                    .onChange(of: launchAtLogin) { _, enabled in setLaunchAtLogin(enabled) }
                Divider()
                Button("Quit AI Meter") { NSApplication.shared.terminate(nil) }
                    .keyboardShortcut("q")
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .menuIndicator(.hidden)
            .fixedSize()
            .help("More")
        }
        .buttonStyle(.borderless)
    }

    private func setLaunchAtLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            launchAtLogin = SMAppService.mainApp.status == .enabled
        }
    }
}
