// swift-tools-version: 6.0
import PackageDescription
let package = Package(name: "CodexUsage", platforms: [.macOS(.v14)], products: [
    .library(name: "UsageCore", targets: ["UsageCore"]),
    .library(name: "UsagePresentation", targets: ["UsagePresentation"]),
    .executable(name: "CodexUsage", targets: ["CodexUsage"])
], targets: [
    .target(name: "UsageCore"),
    .target(name: "UsagePresentation", dependencies: ["UsageCore"]),
    .executableTarget(name: "CodexUsage", dependencies: ["UsageCore", "UsagePresentation"]),
    .executableTarget(name: "UsageCoreChecks", dependencies: ["UsageCore", "UsagePresentation"], path: "Tests/UsageCoreTests")
])
