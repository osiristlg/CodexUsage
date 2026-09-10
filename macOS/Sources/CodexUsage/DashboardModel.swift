import SwiftUI
import AppKit
import UsageCore

@MainActor final class DashboardModel: ObservableObject {
    @Published var settings = LocalStore.load("mac-settings.json", as: UsageCore.Settings.self) ?? UsageCore.Settings()
    @Published var points: [UsagePoint] = []
    @Published var network = NetworkState()
    @Published var busy = false
    @Published var status = "Ready"
    @Published var networkStatus = "Reporting disabled"
    @Published var lastRefresh: Date?
    @Published var selectedDate: Date? = nil
    @Published var hoveredDate: Date? = nil
    @Published var hoveredHour: Int? = nil
    @Published var filesScanned = 0
    private var lastSnapshot: Date?
    private var snapshotError: String?
    private var cachedCredential: (account: String, key: Data)?
    private var blockedCredentialAccount: String?
    var selectedDay: Date { Calendar.current.startOfDay(for: selectedDate ?? Date()) }
    var selectedPoints: [UsagePoint] {
        points.filter { Calendar.current.isDate($0.time, inSameDayAs: selectedDay) }
    }
    var todayTokens: Tokens {
        points.filter { Calendar.current.isDateInToday($0.time) }.reduce(Tokens()) { $0 + $1.tokens }
    }
    var visibleCombined: Tokens? {
        guard settings.reportingEnabled, let day = network.combinedDay, Calendar.current.isDateInToday(day) else { return nil }
        return network.combined
    }
    init() {
        if !DashboardTheme.all.contains(where: { $0.id == settings.appearance }) {
            settings.appearance = DashboardTheme.all[0].id
            try? LocalStore.save(settings, name: "mac-settings.json")
        }
        if LocalStore.load("mac-settings.json", as: UsageCore.Settings.self) == nil {
            do { try LocalStore.save(settings, name: "mac-settings.json") }
            catch { status = "Could not save initial settings: \(error.localizedDescription)" }
        }
        points = LocalStore.load("mac-history.json", as: [UsagePoint].self) ?? []
        network = LocalStore.load("mac-network-state.json", as: NetworkState.self) ?? NetworkState()
        networkStatus = settings.reportingEnabled ? "Waiting for refresh" : "Reporting disabled"
    }
    func run() async {
        await refresh()
        while !Task.isCancelled {
            do { try await Task.sleep(for: .seconds(max(15, settings.refreshSeconds))) } catch { return }
            await refresh()
        }
    }
    func refresh(force: Bool = false) async {
        if force {
            network.needsFullSync = true
            do { try LocalStore.save(network, name: "mac-network-state.json") }
            catch { status = "Could not save rebuild request: \(error.localizedDescription)"; return }
        }
        guard !busy else { return }
        busy = true
        defer { busy = false }
        status = "Scanning local sessions…"
        let config = settings
        let now = Date()
        let cal = Calendar.current
        let start = cal.date(byAdding: .day, value: -29, to: cal.startOfDay(for: now))!
        let end = cal.date(byAdding: .day, value: 1, to: cal.startOfDay(for: now))!
        do {
            let scan = try await Task.detached(priority: .utility) {
                try LogScanner.scan(folder: URL(fileURLWithPath: config.sessionsFolder), start: start, end: end)
            }.value
            guard scan.unreadableFiles == 0 else {
                throw UsageError.invalid("\(scan.unreadableFiles) log files could not be read. Keeping previous totals; sync will retry.")
            }
            points = scan.points
            filesScanned = scan.files
            try LocalStore.save(points, name: "mac-history.json")
            lastRefresh = now
            status = "\(scan.files) sessions · \(scan.points.count) responses"
            if scan.malformedRecords > 0 { status += " · \(scan.malformedRecords) incomplete records skipped" }
            if config.reportingEnabled {
                do {
                    let key = try credentialKey(account: config.credentialAccount)
                    let forceFull = network.needsFullSync == true
                    let next = try await SyncEngine.sync(settings: config, points: points, previous: network, key: key, force: forceFull)
                    try LocalStore.save(next, name: "mac-network-state.json")
                    network = next
                    networkStatus = "Encrypted sync complete · " + Date().formatted(date: .omitted, time: .shortened)
                } catch { networkStatus = "Sync failed: \(error.localizedDescription)" }
            } else { networkStatus = "Reporting disabled" }
            if !config.snapshotFolder.isEmpty,
               lastSnapshot == nil || Date().timeIntervalSince(lastSnapshot!) >= Double(config.snapshotMinutes * 60) {
                exportSnapshot()
            }
        } catch { status = error.localizedDescription }
    }
    func save(_ draft: UsageCore.Settings) throws {
        guard !busy else { throw UsageError.invalid("Wait for the current refresh to finish.") }
        guard !draft.sessionsFolder.isEmpty, draft.refreshSeconds >= 15, draft.snapshotMinutes >= 1 else {
            throw UsageError.invalid("Check the session folder and refresh intervals.")
        }
        if draft.reportingEnabled {
            _ = try NetworkClient.endpoint(draft.receiverURL, path: "health")
            _ = try credentialKey(account: draft.credentialAccount)
            guard !draft.machineName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, draft.machineName.utf16.count <= 100 else {
                throw UsageError.invalid("Use a machine name of 1–100 characters.")
            }
        }
        if settings.credentialAccount != draft.credentialAccount || settings.projectPrivacy != draft.projectPrivacy ||
            settings.sessionsFolder != draft.sessionsFolder {
            if settings.credentialAccount != draft.credentialAccount {
                cachedCredential = nil; blockedCredentialAccount = nil
            }
            network = NetworkState(); network.needsFullSync = true
            try LocalStore.save(network, name: "mac-network-state.json")
        }
        try LocalStore.save(draft, name: "mac-settings.json")
        settings = draft
        networkStatus = draft.reportingEnabled ? "Waiting for refresh" : "Reporting disabled"
        Task { await refresh() }
    }
    func pairAndTest(_ draft: UsageCore.Settings, passphrase: String) async throws {
        let key: Data
        if passphrase.isEmpty { key = try credentialKey(account: draft.credentialAccount) }
        else {
            key = try await Task.detached(priority: .userInitiated) {
                try await NetworkClient.pair(settings: draft, passphrase: passphrase)
            }.value
        }
        _ = try await SyncEngine.sync(settings: draft, points: [], previous: NetworkState(), key: key, query: true)
        try KeychainStore.save(key, account: draft.credentialAccount)
        cachedCredential = (draft.credentialAccount, key); blockedCredentialAccount = nil
    }
    private func credentialKey(account: String) throws -> Data {
        if let cachedCredential, cachedCredential.account == account { return cachedCredential.key }
        if blockedCredentialAccount == account {
            throw UsageError.invalid("The saved network credential needs repair. Re-enter the receiver shared passphrase in Settings; no Keychain password is required.")
        }
        do {
            let key = try KeychainStore.read(account: account)
            cachedCredential = (account, key)
            return key
        } catch {
            blockedCredentialAccount = account
            throw error
        }
    }
    func exportSnapshot() {
        do {
            guard !settings.snapshotFolder.isEmpty else { throw UsageError.invalid("Choose a snapshot folder in Settings.") }
            let view = SnapshotView(points: points, total: todayTokens, hideProjects: settings.hideSnapshotProjects)
                .frame(width: 1100).padding(32).background(Color(nsColor: .windowBackgroundColor))
                .environment(\.colorScheme, .dark)
            let renderer = ImageRenderer(content: view); renderer.scale = 2
            guard let image = renderer.nsImage, let tiff = image.tiffRepresentation,
                  let bitmap = NSBitmapImageRep(data: tiff), let png = bitmap.representation(using: .png, properties: [:]) else {
                throw UsageError.invalid("Snapshot rendering failed.")
            }
            let url = URL(fileURLWithPath: settings.snapshotFolder).appendingPathComponent("codex-usage-latest.png")
            try png.write(to: url, options: .atomic)
            lastSnapshot = Date(); snapshotError = nil
            status += " · Snapshot saved"
        } catch { snapshotError = error.localizedDescription; status += " · Snapshot: \(error.localizedDescription)" }
    }
}
