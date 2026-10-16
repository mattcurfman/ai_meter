// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

import SwiftUI

struct BeerGlass: View {
    let remaining: Double
    var monochrome = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        GeometryReader { proxy in
            let fraction = min(1, max(0, remaining / 100))
            PintShape()
                .fill(.primary.opacity(0.04))
                .overlay(alignment: .bottom) {
                    Rectangle()
                        .fill(monochrome ? Color.primary : Color(red: 0.92, green: 0.57, blue: 0.08))
                        .overlay(alignment: .top) {
                            if fraction > 0 && !monochrome {
                                Rectangle()
                                    .fill(Color(red: 1, green: 0.94, blue: 0.76))
                                    .frame(height: min(5, proxy.size.height * fraction))
                            }
                        }
                        .frame(height: proxy.size.height * fraction)
                }
                .clipShape(PintShape())
                .overlay { PintShape().stroke(.primary.opacity(0.6), lineWidth: 1.2) }
                .padding(1)
                .animation(reduceMotion ? nil : .easeInOut(duration: 0.8), value: remaining)
        }
        .accessibilityHidden(true)
    }
}

private struct PintShape: Shape {
    func path(in rect: CGRect) -> Path {
        Path { path in
            path.move(to: CGPoint(x: rect.minX, y: rect.minY))
            path.addLine(to: CGPoint(x: rect.maxX, y: rect.minY))
            path.addLine(to: CGPoint(x: rect.maxX - rect.width * 0.15, y: rect.maxY))
            path.addLine(to: CGPoint(x: rect.minX + rect.width * 0.15, y: rect.maxY))
            path.closeSubpath()
        }
    }
}
