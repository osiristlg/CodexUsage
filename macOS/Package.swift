// swift-tools-version: 6.0
import PackageDescription
let package = Package(name: "CodexUsage", platforms: [.macOS(.v14)], products: [
    .library(name: "UsageCore", targets: ["UsageCore"]),
    .executable(name: "CodexUsage", targets: ["CodexUsage"])
], targets: [
    .target(name: "UsageCore"),
    .executableTarget(name: "CodexUsage", dependencies: ["UsageCore"]),
    .executableTarget(name: "UsageCoreChecks", dependencies: ["UsageCore"], path: "Tests/UsageCoreTests")
])
