import Foundation
import UsageCore

struct ReceiverConfigurationTests {
    func run() throws {
        var configuration = ReceiverConfiguration(bindAddress: "192.168.1.20", port: 4747,
                                                  allowedSubnets: ["192.168.1.0/24"])
        try configuration.register(clientId: "mac-test", machineName: "Test Mac", passphrase: "portable-test-vector")
        expectEqual(configuration.clients.count, 1)
        expectEqual(Data(base64Encoded: configuration.clients[0].salt)?.count, 16)
        expectEqual(Data(base64Encoded: configuration.clients[0].key)?.count, 32)

        let encoded = try JSONEncoder().encode(configuration)
        let text = String(decoding: encoded, as: UTF8.self)
        precondition(text.contains("\"BindAddress\"")); precondition(text.contains("\"ClientId\""))
        expectEqual(try JSONDecoder().decode(ReceiverConfiguration.self, from: encoded), configuration)

        let oldSalt = configuration.clients[0].salt
        try configuration.rotate(clientIds: ["mac-test"], passphrase: "replacement passphrase")
        precondition(configuration.clients[0].salt != oldSalt)
        expectThrows(try ReceiverConfiguration(bindAddress: "0.0.0.0", allowedSubnets: ["0.0.0.0/0"]).validated())
        expectThrows(try ReceiverConfiguration(bindAddress: "localhost", allowedSubnets: ["127.0.0.0/8"]).validated())
    }
}
