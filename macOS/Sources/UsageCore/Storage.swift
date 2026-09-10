import Foundation
import Security

public struct Settings: Codable, Sendable {
    public var sessionsFolder = LogScanner.defaultFolder.path
    public var refreshSeconds = 60
    public var receiverURL = "http://127.0.0.1:4747"
    public var clientId = "mac-" + UUID().uuidString.lowercased()
    public var machineName = Host.current().localizedName ?? "Mac"
    public var reportingEnabled = false
    public var projectPrivacy = ProjectPrivacy.anonymous
    public var appearance = "system"
    public var snapshotFolder = ""
    public var snapshotMinutes = 15
    public var hideSnapshotProjects = true
    public init() {}
    public var credentialAccount: String { receiverURL.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "|" + clientId }
}
public struct NetworkState: Codable, Sendable {
    public var needsFullSync: Bool?
    public var lastSuccess: Date?
    public var lastFull: Date?
    public var combinedDay: Date?
    public var combined: Tokens?
    public var machines: [String: Tokens] = [:]
    public init() {}
}
public enum LocalStore {
    public static var folder: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Codex Usage")
    }
    public static func load<T: Decodable>(_ name: String, as type: T.Type) -> T? {
        guard let data = try? Data(contentsOf: folder.appendingPathComponent(name)) else { return nil }
        return try? JSONDecoder().decode(type, from: data)
    }
    public static func save<T: Encodable>(_ value: T, name: String) throws {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true,
                                               attributes: [.posixPermissions: 0o700])
        try JSONEncoder().encode(value).write(to: folder.appendingPathComponent(name), options: .atomic)
    }
}
public enum KeychainStore {
    private static func query(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "com.codexusage.mac.protocol-v1",
         kSecAttrAccount as String: account]
    }
    public static func save(_ key: Data, account: String) throws {
        guard key.count == 32 else { throw UsageError.invalid("Invalid derived key.") }
        let q = query(account)
        let attributes: [String: Any] = [kSecValueData as String: key,
                                        kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly]
        let update = SecItemUpdate(q as CFDictionary, attributes as CFDictionary)
        if update == errSecItemNotFound {
            let add = SecItemAdd(q.merging(attributes) { _, new in new } as CFDictionary, nil)
            guard add == errSecSuccess else { throw error(add) }
        } else if update != errSecSuccess { throw error(update) }
    }
    public static func read(account: String) throws -> Data {
        var q = query(account); q[kSecReturnData as String] = true; q[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(q as CFDictionary, &result)
        guard status == errSecSuccess, let key = result as? Data, key.count == 32 else { throw error(status) }
        return key
    }
    public static func remove(account: String) throws {
        let status = SecItemDelete(query(account) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw error(status) }
    }
    private static func error(_ status: OSStatus) -> UsageError {
        .invalid("Keychain: " + ((SecCopyErrorMessageString(status, nil) as String?) ?? "credential unavailable"))
    }
}
