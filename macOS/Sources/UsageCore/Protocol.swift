import Foundation
import CryptoKit
import CommonCrypto

public struct Tokens: Codable, Equatable, Sendable {
    public var input: Int64 = 0
    public var cachedInput: Int64 = 0
    public var output: Int64 = 0
    public var reasoning: Int64 = 0
    public var responses: Int64 = 0
    public var total: Int64 { input + output }
    public init(input: Int64 = 0, cachedInput: Int64 = 0, output: Int64 = 0, reasoning: Int64 = 0, responses: Int64 = 0) {
        self.input = input; self.cachedInput = cachedInput; self.output = output
        self.reasoning = reasoning; self.responses = responses
    }
    public static func + (a: Self, b: Self) -> Self {
        .init(input: a.input + b.input, cachedInput: a.cachedInput + b.cachedInput,
              output: a.output + b.output, reasoning: a.reasoning + b.reasoning, responses: a.responses + b.responses)
    }
    enum CodingKeys: String, CodingKey { case input, cachedInput, output, reasoning, responses, total }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        input = try c.decode(Int64.self, forKey: .input); cachedInput = try c.decode(Int64.self, forKey: .cachedInput)
        output = try c.decode(Int64.self, forKey: .output); reasoning = try c.decode(Int64.self, forKey: .reasoning)
        responses = try c.decode(Int64.self, forKey: .responses)
    }
    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(input, forKey: .input); try c.encode(cachedInput, forKey: .cachedInput)
        try c.encode(output, forKey: .output); try c.encode(reasoning, forKey: .reasoning)
        try c.encode(responses, forKey: .responses); try c.encode(total, forKey: .total)
    }
}
public struct AggregateRow: Codable, Equatable, Sendable {
    public var bucketStartUtc: String
    public var model: String
    public var project: String
    public var tokens: Tokens
    public init(bucketStartUtc: String, model: String, project: String, tokens: Tokens) {
        self.bucketStartUtc = bucketStartUtc; self.model = model; self.project = project; self.tokens = tokens
    }
}
public struct SyncPayload: Codable, Sendable {
    public var kind: String
    public var machineName: String
    public var rangeStartUtc: String
    public var rangeEndUtc: String
    public var combinedStartUtc: String
    public var combinedEndUtc: String
    public var rows: [AggregateRow]
    public init(kind: String, machineName: String, rangeStartUtc: String, rangeEndUtc: String,
                combinedStartUtc: String, combinedEndUtc: String, rows: [AggregateRow]) {
        self.kind = kind; self.machineName = machineName; self.rangeStartUtc = rangeStartUtc
        self.rangeEndUtc = rangeEndUtc; self.combinedStartUtc = combinedStartUtc
        self.combinedEndUtc = combinedEndUtc; self.rows = rows
    }
}
public struct ExchangeReply: Codable, Sendable {
    public var accepted: Bool
    public var message: String
    public var receivedAtUtc: String
    public var combined: Tokens
    public var machines: [String: Tokens]
}
public struct Envelope: Codable, Sendable {
    public var version: Int
    public var clientId: String
    public var requestId: String
    public var createdAtUtc: String
    public var nonce: String
    public var ciphertext: String
    public var tag: String
    public init(version: Int, clientId: String, requestId: String, createdAtUtc: String, nonce: String, ciphertext: String, tag: String) {
        self.version = version; self.clientId = clientId; self.requestId = requestId; self.createdAtUtc = createdAtUtc
        self.nonce = nonce; self.ciphertext = ciphertext; self.tag = tag
    }
}
public enum UsageError: Error, LocalizedError {
    case invalid(String)
    public var errorDescription: String? { if case .invalid(let text) = self { return text }; return nil }
}
public enum WireTime {
    public static func string(_ date: Date) -> String {
        let formatter = ISO8601DateFormatter()
        return formatter.string(from: date) // Whole seconds are sufficient for outgoing requests.
    }
    public static func date(_ string: String) -> Date? {
        let f = ISO8601DateFormatter()
        f.formatOptions.insert(.withFractionalSeconds)
        return f.date(from: string) ?? ISO8601DateFormatter().date(from: string)
    }
    // Preserve all seven .NET ticks; converting through Date would lose precision.
    public static func authenticated(_ string: String) throws -> String {
        guard string.hasSuffix("Z"), date(string) != nil else { throw UsageError.invalid("Expected a UTC timestamp.") }
        let parts = string.dropLast().split(separator: ".", omittingEmptySubsequences: false)
        let fraction = parts.count == 2 ? String(parts[1]) : ""
        guard parts.count <= 2, fraction.count <= 7, fraction.allSatisfy(\.isNumber) else {
            throw UsageError.invalid("Invalid timestamp precision.")
        }
        return String(parts[0]) + "." + fraction + String(repeating: "0", count: 7 - fraction.count) + "Z"
    }
}
public enum AggregateProtocol {
    public static func deriveKey(passphrase: String, salt: Data) throws -> Data {
        guard !passphrase.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, salt.count == 16 else {
            throw UsageError.invalid("A passphrase and 16-byte salt are required.")
        }
        let password = Array(passphrase.precomposedStringWithCompatibilityMapping.utf8)
        var result = [UInt8](repeating: 0, count: 32)
        let status = password.withUnsafeBytes { p in salt.withUnsafeBytes { s in
            CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2), p.baseAddress!.assumingMemoryBound(to: Int8.self), password.count,
                                s.baseAddress!.assumingMemoryBound(to: UInt8.self), salt.count,
                                CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), 600_000, &result, 32)
        }}
        guard status == kCCSuccess else { throw UsageError.invalid("Key derivation failed.") }
        return Data(result)
    }
    private static func aad(_ e: Envelope, key: Data) throws -> Data {
        guard e.version == 1, key.count == 32, !e.clientId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              e.clientId.utf16.count <= 80, UUID(uuidString: e.requestId) != nil else {
            throw UsageError.invalid("Invalid protocol envelope.")
        }
        return Data("1\n\(e.clientId)\n\(e.requestId)\n\(try WireTime.authenticated(e.createdAtUtc))".utf8)
    }
    public static func encrypt<T: Encodable>(_ payload: T, clientId: String, key: Data, now: Date = Date()) throws -> Envelope {
        try seal(JSONEncoder().encode(payload), clientId: clientId, requestId: UUID().uuidString.lowercased(),
                 createdAtUtc: WireTime.string(now), key: key)
    }
    public static func seal(_ plaintext: Data, clientId: String, requestId: String, createdAtUtc: String,
                            key: Data, nonce: Data? = nil) throws -> Envelope {
        var e = Envelope(version: 1, clientId: clientId, requestId: requestId, createdAtUtc: createdAtUtc,
                         nonce: "", ciphertext: "", tag: "")
        let associated = try aad(e, key: key)
        let n = try nonce.map { try AES.GCM.Nonce(data: $0) } ?? AES.GCM.Nonce()
        let box = try AES.GCM.seal(plaintext, using: SymmetricKey(data: key), nonce: n, authenticating: associated)
        e.nonce = Data(box.nonce).base64EncodedString(); e.ciphertext = box.ciphertext.base64EncodedString()
        e.tag = box.tag.base64EncodedString()
        return e
    }
    public static func open(_ envelope: Envelope, key: Data) throws -> Data {
        let associated = try aad(envelope, key: key)
        guard let n = Data(base64Encoded: envelope.nonce), n.count == 12,
              let c = Data(base64Encoded: envelope.ciphertext), let t = Data(base64Encoded: envelope.tag), t.count == 16 else {
            throw UsageError.invalid("Invalid encrypted data.")
        }
        return try AES.GCM.open(.init(nonce: AES.GCM.Nonce(data: n), ciphertext: c, tag: t),
                                using: SymmetricKey(data: key), authenticating: associated)
    }
}
