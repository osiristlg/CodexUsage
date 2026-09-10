import Foundation
import Security
import LocalAuthentication

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
    private static let service = "com.codexusage.mac.network-key-v1"
    private static let legacyService = "com.codexusage.mac.protocol-v1"
    private static func query(_ account: String, service: String) -> [String: Any] {
        let context = LAContext()
        context.interactionNotAllowed = true
        return [kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: service,
                kSecAttrAccount as String: account,
                kSecUseAuthenticationContext as String: context,
                // Keep the legacy query flag as a second guard for ad-hoc builds on systems
                // that still show an ACL prompt despite interactionNotAllowed.
                kSecUseAuthenticationUI as String: "u_AuthUIF"]
    }
    public static func save(_ key: Data, account: String) throws {
#if CODEX_USAGE_ADHOC
        try DevelopmentCredentialStore.save(key, account: account)
#else
        guard key.count == 32 else { throw UsageError.invalid("Invalid derived key.") }
        let q = query(account, service: service)
        let attributes: [String: Any] = [kSecValueData as String: key,
                                        kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly]
        let update = SecItemUpdate(q as CFDictionary, attributes as CFDictionary)
        if update == errSecSuccess { return }
        if [errSecAuthFailed, errSecInteractionNotAllowed, errSecUserCanceled].contains(update) {
            let removal = SecItemDelete(q as CFDictionary)
            guard removal == errSecSuccess || removal == errSecItemNotFound else { throw error(removal, operation: "remove") }
        } else if update != errSecItemNotFound { throw error(update, operation: "update") }
        let add = SecItemAdd(q.merging(attributes) { _, new in new } as CFDictionary, nil)
        if add == errSecDuplicateItem {
            let retry = SecItemUpdate(q as CFDictionary, attributes as CFDictionary)
            guard retry == errSecSuccess else { throw error(retry, operation: "update") }
        } else {
            guard add == errSecSuccess else { throw error(add, operation: "add") }
        }
#endif
    }
    public static func read(account: String) throws -> Data {
#if CODEX_USAGE_ADHOC
        return try DevelopmentCredentialStore.read(account: account)
#else
        let current = copy(account: account, service: service)
        if current.status == errSecSuccess, let key = current.key { return key }
        guard current.status == errSecItemNotFound else { throw error(current.status, operation: "read") }

        let legacy = copy(account: account, service: legacyService)
        guard legacy.status == errSecSuccess, let key = legacy.key else { throw error(legacy.status, operation: "read") }
        try save(key, account: account)
        try? remove(account: account, service: legacyService)
        return key
#endif
    }
    private static func copy(account: String, service: String) -> (status: OSStatus, key: Data?) {
        var q = query(account, service: service); q[kSecReturnData as String] = true; q[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(q as CFDictionary, &result)
        guard status == errSecSuccess else { return (status, nil) }
        guard let key = result as? Data, key.count == 32 else { return (errSecDecode, nil) }
        return (status, key)
    }
    public static func remove(account: String) throws {
#if CODEX_USAGE_ADHOC
        try DevelopmentCredentialStore.remove(account: account)
#else
        let status = SecItemDelete(query(account, service: service) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw error(status, operation: "remove") }
#endif
    }
    private static func remove(account: String, service: String) throws {
        let status = SecItemDelete(query(account, service: service) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else { throw error(status, operation: "remove") }
    }
    private static func error(_ status: OSStatus, operation: String) -> UsageError {
        if status == errSecItemNotFound {
            return .invalid("Network credential is not configured. Re-enter the receiver shared passphrase in Settings; no Keychain password is required.")
        }
        if [errSecAuthFailed, errSecInteractionNotAllowed, errSecUserCanceled].contains(status) {
            return .invalid("The saved network credential belongs to an earlier development build. Re-enter the receiver shared passphrase in Settings to replace it; no Keychain password is required.")
        }
        return .invalid("Keychain \(operation): " + ((SecCopyErrorMessageString(status, nil) as String?) ?? "credential unavailable"))
    }
}

#if CODEX_USAGE_ADHOC
private enum DevelopmentCredentialStore {
    private static var url: URL {
        if let path = ProcessInfo.processInfo.environment["CODEX_USAGE_CREDENTIALS"] { return URL(fileURLWithPath: path) }
        return LocalStore.folder.appendingPathComponent("mac-network-credentials.json")
    }

    static func save(_ key: Data, account: String) throws {
        guard key.count == 32 else { throw UsageError.invalid("Invalid derived key.") }
        var values = load()
        values[account] = key.base64EncodedString()
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        try JSONEncoder().encode(values).write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    static func read(account: String) throws -> Data {
        guard let encoded = load()[account], let key = Data(base64Encoded: encoded), key.count == 32 else {
            throw UsageError.invalid("Network credential is not configured. Re-enter the receiver shared passphrase in Settings; no Keychain password is required.")
        }
        return key
    }

    static func remove(account: String) throws {
        var values = load(); values.removeValue(forKey: account)
        if values.isEmpty { try? FileManager.default.removeItem(at: url); return }
        try JSONEncoder().encode(values).write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    private static func load() -> [String: String] {
        guard let data = try? Data(contentsOf: url) else { return [:] }
        return (try? JSONDecoder().decode([String: String].self, from: data)) ?? [:]
    }
}
#endif
