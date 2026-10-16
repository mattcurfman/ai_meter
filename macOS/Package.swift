// swift-tools-version: 6.0
// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

import PackageDescription

let package = Package(
    name: "AIMeter",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(name: "AIMeter", path: "Sources/AIMeter")
    ]
)
