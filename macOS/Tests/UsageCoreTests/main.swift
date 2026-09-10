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
try await networkChecks()
print("PASS: scanner precedence, privacy, hour boundaries, local 2am scheduling, DST and retry identity")

if CommandLine.arguments.contains("--scan-local") {
    let now = Date()
    let start = Calendar.current.date(byAdding: .day, value: -29, to: Calendar.current.startOfDay(for: now))!
    let result = try LogScanner.scan(folder: LogScanner.defaultFolder, start: start, end: now)
    print("Local read-only scan: \(result.files) files, \(result.points.count) derived points, \(result.unreadableFiles) unreadable, \(result.malformedRecords) malformed")
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
    print("PASS: temporary Keychain credential create, update, read and delete")
}
