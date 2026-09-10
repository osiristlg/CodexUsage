import Foundation
import Network
import Security

public struct ReceiverClient: Codable, Identifiable, Equatable, Sendable {
    public var clientId: String
    public var machineName: String
    public var salt: String
    public var key: String
    public var enabled: Bool
    public var id: String { clientId }

    public init(clientId: String, machineName: String, salt: String, key: String, enabled: Bool = true) {
        self.clientId = clientId; self.machineName = machineName; self.salt = salt; self.key = key; self.enabled = enabled
    }

    enum CodingKeys: String, CodingKey {
        case clientId = "ClientId", machineName = "MachineName", salt = "Salt", key = "Key", enabled = "Enabled"
    }
}

public struct ReceiverConfiguration: Codable, Equatable, Sendable {
    public var bindAddress: String
    public var port: Int
    public var allowedSubnets: [String]
    public var clients: [ReceiverClient]
    public var maxPayloadBytes: Int
    public var maxRowsPerRequest: Int

    public init(bindAddress: String = "127.0.0.1", port: Int = 4747,
                allowedSubnets: [String] = ["127.0.0.0/8", "::1/128"], clients: [ReceiverClient] = [],
                maxPayloadBytes: Int = 16 * 1024 * 1024, maxRowsPerRequest: Int = 100_000) {
        self.bindAddress = bindAddress; self.port = port; self.allowedSubnets = allowedSubnets; self.clients = clients
        self.maxPayloadBytes = maxPayloadBytes; self.maxRowsPerRequest = maxRowsPerRequest
    }

    enum CodingKeys: String, CodingKey {
        case bindAddress = "BindAddress", port = "Port", allowedSubnets = "AllowedSubnets", clients = "Clients"
        case maxPayloadBytes = "MaxPayloadBytes", maxRowsPerRequest = "MaxRowsPerRequest"
    }

    public mutating func register(clientId: String, machineName: String, passphrase: String) throws {
        let id = clientId.trimmingCharacters(in: .whitespacesAndNewlines)
        let name = machineName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard (1...80).contains(id.utf16.count), (1...100).contains(name.utf16.count) else {
            throw UsageError.invalid("Use a client ID of 1–80 characters and a machine name of 1–100 characters.")
        }
        let credential = try Self.credential(passphrase: passphrase)
        clients.removeAll { $0.clientId.caseInsensitiveCompare(id) == .orderedSame }
        clients.append(ReceiverClient(clientId: id, machineName: name, salt: credential.salt, key: credential.key))
    }

    public mutating func rotate(clientIds: Set<String>, passphrase: String) throws {
        guard !clientIds.isEmpty else { throw UsageError.invalid("Select at least one receiver client.") }
        var changed = false
        for index in clients.indices where clientIds.contains(clients[index].clientId) {
            let credential = try Self.credential(passphrase: passphrase)
            clients[index].salt = credential.salt; clients[index].key = credential.key
            changed = true
        }
        guard changed else { throw UsageError.invalid("The selected receiver clients were not found.") }
    }

    public func validated() throws -> ReceiverConfiguration {
        guard IPv4Address(bindAddress) != nil || IPv6Address(bindAddress) != nil else {
            throw UsageError.invalid("Bind address must be one IPv4 or IPv6 address.")
        }
        guard (1...65535).contains(port) else { throw UsageError.invalid("Port must be between 1 and 65535.") }
        guard !allowedSubnets.isEmpty else { throw UsageError.invalid("Add at least one explicit allowed subnet.") }
        for subnet in allowedSubnets {
            let pieces = subnet.split(separator: "/", omittingEmptySubsequences: false)
            guard pieces.count == 2, let prefix = Int(pieces[1]), prefix > 0,
                  (IPv4Address(String(pieces[0])) != nil && prefix <= 32) ||
                  (IPv6Address(String(pieces[0])) != nil && prefix <= 128) else {
                throw UsageError.invalid("Invalid or unrestricted subnet: \(subnet). Use CIDR such as 192.168.1.0/24.")
            }
        }
        guard clients.allSatisfy({ (1...80).contains($0.clientId.utf16.count) && (1...100).contains($0.machineName.utf16.count) }) else {
            throw UsageError.invalid("A receiver client has an invalid ID or machine name.")
        }
        guard clients.allSatisfy({ Data(base64Encoded: $0.salt)?.count == 16 && Data(base64Encoded: $0.key)?.count == 32 }) else {
            throw UsageError.invalid("A receiver client credential is invalid.")
        }
        return self
    }

    private static func credential(passphrase: String) throws -> (salt: String, key: String) {
        guard !passphrase.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw UsageError.invalid("Enter a receiver shared passphrase.")
        }
        var bytes = [UInt8](repeating: 0, count: 16)
        guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else {
            throw UsageError.invalid("Could not create a secure receiver credential.")
        }
        let salt = Data(bytes)
        return (salt.base64EncodedString(), try AggregateProtocol.deriveKey(passphrase: passphrase, salt: salt).base64EncodedString())
    }
}

public enum ReceiverConfigurationStore {
    public static var folder: URL { LocalStore.folder.appendingPathComponent("Receiver", isDirectory: true) }
    public static var settingsURL: URL { folder.appendingPathComponent("receiver-settings.json") }

    public static func load() throws -> ReceiverConfiguration {
        guard FileManager.default.fileExists(atPath: settingsURL.path) else { return ReceiverConfiguration() }
        return try JSONDecoder().decode(ReceiverConfiguration.self, from: Data(contentsOf: settingsURL))
    }

    public static func save(_ configuration: ReceiverConfiguration) throws {
        let checked = try configuration.validated()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(checked).write(to: settingsURL, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: settingsURL.path)
    }

    public static func initializeIfNeeded() throws -> ReceiverConfiguration {
        let configuration = try load()
        if !FileManager.default.fileExists(atPath: settingsURL.path) { try save(configuration) }
        return configuration
    }
}
