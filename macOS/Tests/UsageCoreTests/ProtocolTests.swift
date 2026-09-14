import Foundation
import UsageCore

struct ProtocolTests {
    func testPublishedWindowsVector() throws {
        let key = try AggregateProtocol.deriveKey(passphrase: "portable-test-vector", salt: Data(0..<16))
        expectEqual(key.map { String(format: "%02x", $0) }.joined(), "94446dc0d561a436365c222aca9f1d5d2e1645816867b28054902c14dd2a9a75")
        let envelope = Envelope(version: 1, clientId: "desktop-a7f3", requestId: "12345678-1234-1234-1234-123456789abc",
            createdAtUtc: "2026-09-10T12:34:56Z", nonce: "EBESExQVFhcYGRob", ciphertext: "E5b4y8RZ4qxL1NMnnqcOL+3oupLpKme4knhUrPc/rusKe1+Mifo99zvn6BHMe27ZI1P73CeNrOgvDb1P7T8ka7PL4chu6eNvl0qKqek2txyEv1x1vXdivqon+5eTFOAPFScGf6S9Qs6VvtGIvKzp0yOdWfT+QpRkoXBWVTlaHU7ZmfZT4X/YNzOy9pONbrzM+oDOpx9diaVHMFRuGZQB/NlO9jLy2Z/nLCWPxRMdbcPfqpSG0ev/OOVmZU+m+AbMe/LEjDP/kBs0DUJJpbsgtIvnuqHNDRCNdj9BZahOUCTcBY1vZP/OEIlv1y4bFhOWppXgHXAD6slcIS29TcCgxwOMgqCj13vk8FYogSUMlphOoVVvgWM7z8fkZUSlVXaaaZ+fv1ZzKZbhE9iUy/Hb+y0FRwuN6P4bAvG4jFda6FaM9DshXdZzuHpVtKhjzxAoIvBp7XWZmjTTM2sf07NPzdhXDSG0n35tHcFUme+mMyCSTXiXkBN7+yLrJrHSU3swAt0RO4JfsK0r6YQ=", tag: "NVjThp5uoiRH5xlmQzfR6A==")
        let plaintext = try AggregateProtocol.open(envelope, key: key)
        let payload = try JSONDecoder().decode(SyncPayload.self, from: plaintext)
        expectEqual(payload.kind, "incremental")
        expectEqual(payload.rows.first?.tokens, Tokens(input: 100, cachedInput: 80, output: 20, reasoning: 5, responses: 1))
        expectEqual(payload.rows.first?.projectId, nil)
        expectEqual(payload.rows.first?.effort, nil)
        let sealed = try AggregateProtocol.seal(plaintext, clientId: envelope.clientId, requestId: envelope.requestId,
            createdAtUtc: envelope.createdAtUtc, key: key, nonce: Data(16..<28))
        expectEqual(sealed.ciphertext, envelope.ciphertext)
        expectEqual(sealed.tag, envelope.tag)
        let swiftJSON = try JSONEncoder().encode(payload)
        expectEqual(try JSONSerialization.jsonObject(with: swiftJSON) as? NSDictionary,
                    try JSONSerialization.jsonObject(with: plaintext) as? NSDictionary)
        var tampered = envelope
        tampered.clientId = "someone-else"
        expectThrows(try AggregateProtocol.open(tampered, key: key))
        expectThrows(try AggregateProtocol.open(envelope, key: Data(repeating: 0, count: 32)))
        tampered = envelope; tampered.version = 2
        expectThrows(try AggregateProtocol.open(tampered, key: key))
    }
    func testNormalizationAndTimestamp() throws {
        expectEqual(try AggregateProtocol.deriveKey(passphrase: "ＡＢＣ", salt: Data(0..<16)),
                       try AggregateProtocol.deriveKey(passphrase: "ABC", salt: Data(0..<16)))
        expectEqual(try WireTime.authenticated("2026-09-10T12:34:56.1234567Z"), "2026-09-10T12:34:56.1234567Z")
        expectEqual(try WireTime.authenticated("2026-09-10T12:34:56.12Z"), "2026-09-10T12:34:56.1200000Z")
    }
}
