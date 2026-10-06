import Foundation
import UsageCore
func expectEqual<T: Equatable>(_ actual: T, _ expected: T, file: StaticString = #file, line: UInt = #line) {
    precondition(actual == expected, "Expected \(expected), got \(actual)", file: file, line: line)
}
func expectThrows<T>(_ value: @autoclosure () throws -> T, file: StaticString = #file, line: UInt = #line) {
    do { _ = try value() } catch { return }
    preconditionFailure("Expected rejection", file: file, line: line)
}
try ProtocolTests().testPublishedWindowsVector()
try ProtocolTests().testNormalizationAndTimestamp()
print("PASS: Windows golden vector, tampering, wrong key, version, NFKC and timestamp checks")
try ScannerTests().run()
try await cachedScannerChecks()
try await networkChecks()
print("PASS: scanner precedence, privacy, hour boundaries, local 2am scheduling, DST and retry identity")
try ReceiverConfigurationTests().run()
print("PASS: receiver settings schema, validation, registration and credential rotation")
try PresentationTests().run()
print("PASS: daily/hourly efforts, rolling project/model totals and shares, remote cache and range isolation")

if CommandLine.arguments.contains("--scan-local") {
    let now = Date()
    let start = Calendar.current.date(byAdding: .day, value: -29, to: Calendar.current.startOfDay(for: now))!
    let result = try LogScanner.scan(folder: LogScanner.defaultFolder, start: start, end: now)
    print("Local read-only scan: \(result.files) files, \(result.points.count) derived points, \(result.unreadableFiles) unreadable, \(result.malformedRecords) malformed")
}
if CommandLine.arguments.contains("--profile-local") {
    let calendar = Calendar.current
    let today = calendar.startOfDay(for: Date())
    let start = calendar.date(byAdding: .day, value: -29, to: today)!
    let end = calendar.date(byAdding: .day, value: 1, to: today)!
    let scanner = CachedLogScanner()
    let clock = ContinuousClock()
    for label in ["cold", "warm", "warm"] {
        let began = clock.now
        let result = try await scanner.scan(folder: LogScanner.defaultFolder, start: start, end: end)
        let elapsed = began.duration(to: clock.now)
        let total = result.points.reduce(Int64(0)) { $0 + $1.tokens.total }
        print("PROFILE \(label): \(elapsed), \(result.files) files, \(result.filesParsed) parsed (\(result.filesResumed) tails), \(result.bytesRead) bytes read, \(result.points.count) points, \(total) tokens, \(result.unreadableFiles) unreadable, \(result.malformedRecords) malformed")
    }
}
if CommandLine.arguments.contains("--keychain") {
    let account = "self-test-" + UUID().uuidString
    defer { try? KeychainStore.remove(account: account) }
    try KeychainStore.save(Data(repeating: 42, count: 32), account: account)
    expectEqual(try KeychainStore.read(account: account), Data(repeating: 42, count: 32))
    try KeychainStore.save(Data(repeating: 43, count: 32), account: account)
    expectEqual(try KeychainStore.read(account: account), Data(repeating: 43, count: 32))
    try KeychainStore.remove(account: account)
    expectThrows(try KeychainStore.read(account: account))
    print("PASS: temporary network credential create, update, read and delete")
}
