import Foundation
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
