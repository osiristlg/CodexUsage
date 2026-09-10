import Foundation
import UsageCore

struct ReceiverHealth: Decodable {
    let service: String
    let protocolVersion: Int
    let clients: Int
}

@MainActor final class ReceiverHostModel: ObservableObject {
    @Published var configuration = ReceiverConfiguration()
    @Published var selectedClientIds: Set<String> = []
    @Published var runtimeMessage = "Checking for .NET 10…"
    @Published var statusMessage = "Receiver not initialized"
    @Published var running = false
    @Published var healthy = false
    @Published var clientCount = 0
    @Published var busy = false

    private var process: Process?
    private var dotnetURL: URL?
    private var receiverDLL: URL?

    init() { refreshInstallation() }

    func refreshInstallation() {
        do {
            configuration = try ReceiverConfigurationStore.load()
            statusMessage = FileManager.default.fileExists(atPath: ReceiverConfigurationStore.settingsURL.path)
                ? "Configuration ready" : "Choose Initialize receiver to create local settings"
        } catch { statusMessage = "Settings: \(error.localizedDescription)" }

        dotnetURL = Self.findDotnet()
        receiverDLL = Self.findReceiverDLL()
        guard let dotnetURL else {
            runtimeMessage = ".NET 10 runtime required. Install it from dotnet.microsoft.com, then check again."
            return
        }
        do {
            let result = try Self.capture(dotnetURL, ["--list-runtimes"])
            guard result.contains("Microsoft.NETCore.App 10."), result.contains("Microsoft.AspNetCore.App 10.") else {
                runtimeMessage = ".NET 10 ASP.NET Core runtime required; a different .NET version is installed."
                return
            }
            runtimeMessage = receiverDLL == nil
                ? ".NET 10 is ready, but this development build has no bundled receiver payload."
                : ".NET 10 runtime and receiver payload are ready."
        } catch { runtimeMessage = "Could not inspect .NET: \(error.localizedDescription)" }
    }

    func initializeReceiver() {
        do {
            configuration = try ReceiverConfigurationStore.initializeIfNeeded()
            statusMessage = "Receiver initialized at \(ReceiverConfigurationStore.folder.path)"
        } catch { statusMessage = error.localizedDescription }
    }

    func saveConfiguration(bindAddress: String, portText: String, subnetsText: String) {
        do {
            guard let port = Int(portText) else { throw UsageError.invalid("Enter a numeric receiver port.") }
            var updated = configuration
            updated.bindAddress = bindAddress.trimmingCharacters(in: .whitespacesAndNewlines)
            updated.port = port
            updated.allowedSubnets = subnetsText
                .components(separatedBy: CharacterSet(charactersIn: ",\n"))
                .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
            let endpointChanged = updated.bindAddress != configuration.bindAddress || updated.port != configuration.port
            try ReceiverConfigurationStore.save(updated)
            configuration = updated
            statusMessage = endpointChanged && running
                ? "Saved. Restart the receiver to apply its bind address or port."
                : "Receiver configuration saved. Credential changes reload while it runs."
        } catch { statusMessage = error.localizedDescription }
    }

    func register(clientId: String, machineName: String, passphrase: String) {
        do {
            var updated = configuration
            try updated.register(clientId: clientId, machineName: machineName, passphrase: passphrase)
            try ReceiverConfigurationStore.save(updated)
            configuration = updated
            selectedClientIds = [clientId.trimmingCharacters(in: .whitespacesAndNewlines)]
            statusMessage = "Client registered. Enter the same passphrase in that client app."
            Task { await checkHealth() }
        } catch { statusMessage = error.localizedDescription }
    }

    func rotateSelected(passphrase: String) {
        do {
            var updated = configuration
            try updated.rotate(clientIds: selectedClientIds, passphrase: passphrase)
            try ReceiverConfigurationStore.save(updated)
            configuration = updated
            statusMessage = "Rotated \(selectedClientIds.count) client credential\(selectedClientIds.count == 1 ? "" : "s"). Enter this passphrase in each selected client app."
            Task { await checkHealth() }
        } catch { statusMessage = error.localizedDescription }
    }

    func setEnabled(_ enabled: Bool, clientId: String) {
        guard let index = configuration.clients.firstIndex(where: { $0.clientId == clientId }) else { return }
        do {
            configuration.clients[index].enabled = enabled
            try ReceiverConfigurationStore.save(configuration)
            statusMessage = enabled ? "Client enabled." : "Client disabled."
            Task { await checkHealth() }
        } catch { statusMessage = error.localizedDescription }
    }

    func start() async {
        guard process == nil else { statusMessage = "Receiver is already managed by this app."; return }
        refreshInstallation()
        guard let dotnetURL else { statusMessage = ".NET 10 ASP.NET Core runtime is required to host the receiver."; return }
        guard let receiverDLL else { statusMessage = "Receiver payload is missing from this app build."; return }
        do {
            configuration = try ReceiverConfigurationStore.initializeIfNeeded()
            let logURL = ReceiverConfigurationStore.folder.appendingPathComponent("receiver.log")
            FileManager.default.createFile(atPath: logURL.path, contents: nil)
            let log = try FileHandle(forWritingTo: logURL)
            let child = Process(); child.executableURL = dotnetURL; child.arguments = [receiverDLL.path]
            var environment = ProcessInfo.processInfo.environment
            environment["CODEX_USAGE_RECEIVER_DATA"] = ReceiverConfigurationStore.folder.path
            environment["DOTNET_NOLOGO"] = "1"
            child.environment = environment; child.standardOutput = log; child.standardError = log
            child.terminationHandler = { [weak self] _ in
                try? log.close()
                Task { @MainActor in
                    self?.process = nil; self?.running = false; self?.healthy = false
                    if self?.statusMessage == "Receiver running" { self?.statusMessage = "Receiver stopped" }
                }
            }
            try child.run(); process = child; running = true; statusMessage = "Starting receiver…"
            try? await Task.sleep(for: .milliseconds(700))
            await checkHealth()
        } catch { statusMessage = "Could not start receiver: \(error.localizedDescription)" }
    }

    func stop() {
        guard let process else { statusMessage = healthy ? "A receiver is running outside this app." : "Receiver is not running."; return }
        process.terminate(); statusMessage = "Stopping receiver…"
    }

    func checkHealth() async {
        busy = true; defer { busy = false }
        var components = URLComponents(); components.scheme = "http"
        components.host = ["0.0.0.0", "::"].contains(configuration.bindAddress) ? "127.0.0.1" : configuration.bindAddress
        components.port = configuration.port; components.path = "/health"
        guard let url = components.url else { healthy = false; return }
        var request = URLRequest(url: url); request.timeoutInterval = 2
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw UsageError.invalid("Health check failed.") }
            let health = try JSONDecoder().decode(ReceiverHealth.self, from: data)
            healthy = health.protocolVersion == 1; clientCount = health.clients
            running = process?.isRunning == true || healthy
            statusMessage = healthy ? "Receiver running" : "Receiver protocol is incompatible"
        } catch {
            healthy = false; clientCount = configuration.clients.filter(\.enabled).count
            running = process?.isRunning == true
            statusMessage = running ? "Receiver process is running, but health is not ready." : "Receiver stopped"
        }
    }

    private static func findDotnet() -> URL? {
        let environment = ProcessInfo.processInfo.environment
        let pathCandidates = (environment["PATH"] ?? "").split(separator: ":").map { String($0) + "/dotnet" }
        let candidates = [environment["CODEX_USAGE_DOTNET"],
                          FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".dotnet/dotnet").path,
                          "/usr/local/share/dotnet/dotnet", "/opt/homebrew/bin/dotnet", "/usr/local/bin/dotnet",
                          "/opt/homebrew/share/dotnet/dotnet"].compactMap { $0 } + pathCandidates
        return candidates.map(URL.init(fileURLWithPath:)).first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    private static func findReceiverDLL() -> URL? {
        var candidates: [URL] = []
        if let override = ProcessInfo.processInfo.environment["CODEX_USAGE_RECEIVER_DLL"] { candidates.append(URL(fileURLWithPath: override)) }
        if let resource = Bundle.main.resourceURL { candidates.append(resource.appendingPathComponent("Receiver/Codex Usage Receiver.dll")) }
        return candidates.first { FileManager.default.fileExists(atPath: $0.path) }
    }

    private static func capture(_ executable: URL, _ arguments: [String]) throws -> String {
        let process = Process(); let pipe = Pipe()
        process.executableURL = executable; process.arguments = arguments; process.standardOutput = pipe; process.standardError = pipe
        try process.run(); process.waitUntilExit()
        return String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
    }
}
