import SwiftUI
import AppKit
import UsageCore

private enum SettingsPage: Int, CaseIterable {
    case dashboard, data, snapshot, network, receiver
    var title: String { ["Dashboard", "Codex Data", "Snapshot Export", "Network Reporting", "Receiver Host"][rawValue] }
    var subtitle: String { ["Tune the live experience without leaving the cockpit.", "Control where local usage is discovered.", "Keep one fresh dashboard image wherever you need it.", "Encrypted aggregate reporting across your private network.", "Host the same private receiver on this Mac with .NET 10."][rawValue] }
    var icon: String { ["rectangle.3.group", "house", "photo", "network", "server.rack"][rawValue] }
}

struct SettingsView: View {
    @ObservedObject var model: DashboardModel
    @Environment(\.dismiss) private var dismiss
    @State private var draft: UsageCore.Settings
    @State private var page = SettingsPage.dashboard
    @State private var passphrase = ""
    @State private var message = ""
    @State private var testing = false
    @State private var snapshotEnabled: Bool
    @State private var receiverBind: String
    @State private var receiverPort: String
    @State private var receiverSubnets: String
    @State private var receiverPassphrase = ""

    init(model: DashboardModel) {
        self.model = model
        _draft = State(initialValue: model.settings)
        _snapshotEnabled = State(initialValue: !model.settings.snapshotFolder.isEmpty)
        _receiverBind = State(initialValue: model.receiverHost.configuration.bindAddress)
        _receiverPort = State(initialValue: String(model.receiverHost.configuration.port))
        _receiverSubnets = State(initialValue: model.receiverHost.configuration.allowedSubnets.joined(separator: "\n"))
    }

    var body: some View {
        let theme = DashboardTheme.resolve(draft.appearance)
        VStack(spacing: 0) {
            header(theme)
            HStack(spacing: 0) {
                rail(theme).frame(width: 230)
                pageContent(theme).frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            footer(theme)
        }
        .frame(width: 1040, height: 760)
        .background(theme.background)
        .foregroundStyle(theme.text)
        .environment(\.dashboardTheme, theme)
        .preferredColorScheme(.dark)
    }

    private func header(_ theme: DashboardTheme) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 3) {
                Text("CODEX  /  CONTROL DECK").font(.system(size: 10, weight: .bold)).tracking(1.3).foregroundStyle(theme.tertiary)
                Text("Settings").font(.system(size: 29, weight: .bold, design: .rounded))
            }
            Spacer()
            Button { dismiss() } label: { Image(systemName: "xmark").frame(width: 18) }
                .buttonStyle(ControlDeckButtonStyle(accent: theme.secondary))
        }
        .padding(.horizontal, 28).frame(height: 78)
        .background(theme.background.opacity(0.82))
        .overlay(alignment: .bottom) { Rectangle().fill(theme.secondary.opacity(0.15)).frame(height: 1) }
    }

    private func rail(_ theme: DashboardTheme) -> some View {
        VStack(spacing: 0) {
            ForEach(SettingsPage.allCases, id: \.rawValue) { item in
                Button { page = item; message = "" } label: {
                    HStack(spacing: 13) {
                        Image(systemName: item.icon).frame(width: 18)
                        Text(item.title)
                        Spacer()
                    }.padding(.horizontal, 16).frame(height: 58)
                }
                .buttonStyle(.plain)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(page == item ? theme.text : theme.muted)
                .background(page == item ? theme.primary.opacity(0.14) : .clear, in: RoundedRectangle(cornerRadius: 4))
                .overlay(alignment: .leading) { if page == item { Rectangle().fill(theme.primary).frame(width: 2).shadow(color: theme.primary, radius: 4) } }
            }
            Spacer()
            VStack(alignment: .leading, spacing: 5) {
                Text("PRIVACY FIRST").font(.system(size: 9, weight: .bold)).tracking(1).foregroundStyle(theme.tertiary)
                Text("Raw logs remain on this Mac.").font(.system(size: 10)).foregroundStyle(theme.muted)
            }.frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.horizontal, 18).padding(.vertical, 24)
        .background(theme.panel.opacity(0.68))
    }

    @ViewBuilder private func pageContent(_ theme: DashboardTheme) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(page.title).font(.system(size: 31, weight: .bold, design: .rounded))
            Text(page.subtitle).font(.system(size: 13)).foregroundStyle(theme.muted).padding(.top, 3).padding(.bottom, 22)
            Group {
                switch page {
                case .dashboard: dashboardPage(theme)
                case .data: dataPage(theme)
                case .snapshot: snapshotPage(theme)
                case .network: networkPage(theme)
                case .receiver: receiverPage(theme)
                }
            }
            Spacer()
            if page == .network && !message.isEmpty {
                Text(message).font(.system(size: 11)).foregroundStyle(message.contains("verified") ? theme.primary : theme.secondary).textSelection(.enabled)
            }
        }.padding(.horizontal, 48).padding(.vertical, 32)
    }

    private func dashboardPage(_ theme: DashboardTheme) -> some View {
        VStack(spacing: 8) {
            SettingCard(title: "COLOR SCHEME", description: "Choose the visual atmosphere.") {
                Picker("", selection: $draft.appearance) { ForEach(DashboardTheme.all) { Text($0.id).tag($0.id) } }
                    .labelsHidden().pickerStyle(.menu).buttonStyle(.plain).controlDeckInput().frame(width: 180)
            }
            SettingCard(title: "REFRESH INTERVAL", description: "How often local session logs are rescanned.") {
                Picker("", selection: $draft.refreshSeconds) {
                    Text("15 seconds").tag(15); Text("30 seconds").tag(30); Text("1 minute").tag(60); Text("5 minutes").tag(300)
                }.labelsHidden().pickerStyle(.menu).buttonStyle(.plain).controlDeckInput().frame(width: 180)
            }
        }
    }

    private func dataPage(_ theme: DashboardTheme) -> some View {
        SettingCard(title: "SESSION LOG LOCATION", description: "Resolved from your home directory by default. No username is hard-coded.", tall: true) {
            VStack(alignment: .leading, spacing: 8) {
                Text(draft.sessionsFolder).font(.system(size: 11, design: .monospaced)).lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                Button("Browse…") { chooseFolder { draft.sessionsFolder = $0 } }.buttonStyle(ControlDeckButtonStyle(accent: theme.secondary))
            }
        }
    }

    private func snapshotPage(_ theme: DashboardTheme) -> some View {
        VStack(spacing: 8) {
            SettingCard(title: "LATEST SNAPSHOT", description: "Export codex-usage-latest.png automatically.") { Toggle("", isOn: $snapshotEnabled).labelsHidden().toggleStyle(.switch).tint(theme.primary) }
            SettingCard(title: "DESTINATION", description: draft.snapshotFolder.isEmpty ? "Local or user-managed synchronized folder." : draft.snapshotFolder, tall: true) {
                Button("Browse…") { chooseFolder { draft.snapshotFolder = $0; snapshotEnabled = true } }.buttonStyle(ControlDeckButtonStyle(accent: theme.secondary))
            }
            SettingCard(title: "CAPTURE INTERVAL", description: "The latest PNG replaces the previous capture.") {
                Picker("", selection: $draft.snapshotMinutes) { ForEach([5, 15, 30, 60], id: \.self) { Text("\($0) minutes").tag($0) } }.labelsHidden().pickerStyle(.menu).buttonStyle(.plain).controlDeckInput().frame(width: 180)
            }
            SettingCard(title: "PROJECT PRIVACY", description: "Replace project names with anonymous labels in exported images.") { Toggle("", isOn: $draft.hideSnapshotProjects).labelsHidden().toggleStyle(.switch).tint(theme.primary) }
        }
    }

    private func networkPage(_ theme: DashboardTheme) -> some View {
        VStack(alignment: .leading, spacing: 11) {
            Text("AGGREGATES ONLY  /  RAW LOGS NEVER LEAVE THIS MACHINE").font(.system(size: 10, weight: .bold)).tracking(0.7).foregroundStyle(theme.tertiary)
            NetworkRow("REPORT TO RECEIVER") { Toggle("", isOn: $draft.reportingEnabled).labelsHidden().toggleStyle(.switch).tint(theme.primary) }
            NetworkRow("CLIENT ID  /  COPY TO RECEIVER") {
                HStack { Text(draft.clientId).font(.system(size: 11, design: .monospaced)).lineLimit(1); Button { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(draft.clientId, forType: .string) } label: { Image(systemName: "doc.on.doc") }.buttonStyle(.plain) }
            }
            NetworkRow("RECEIVER ADDRESS") { TextField("http://host:4747", text: $draft.receiverURL).textFieldStyle(ControlDeckFieldStyle()) }
            NetworkRow("MACHINE NAME") { TextField("Machine name", text: $draft.machineName).textFieldStyle(ControlDeckFieldStyle()) }
            NetworkRow("SHARED PASSPHRASE") { SecureField("Leave blank to keep configured key", text: $passphrase).textFieldStyle(ControlDeckFieldStyle()) }
            NetworkRow("PROJECT DETAIL") {
                Picker("", selection: $draft.projectPrivacy) {
                    Text("Anonymous project IDs").tag(ProjectPrivacy.anonymous); Text("Project names").tag(ProjectPrivacy.names); Text("No project breakdown").tag(ProjectPrivacy.none)
                }.labelsHidden().pickerStyle(.menu).buttonStyle(.plain).controlDeckInput()
            }
            HStack(spacing: 14) {
                Button(testing ? "Testing…" : "Test connection") {
                    testing = true; message = "Testing encrypted exchange…"
                    let secret = passphrase; passphrase = ""
                    Task {
                        do { try await model.pairAndTest(draft, passphrase: secret); message = "Encrypted connection verified" }
                        catch { message = error.localizedDescription }
                        testing = false
                    }
                }.buttonStyle(ControlDeckButtonStyle(accent: theme.secondary)).disabled(testing)
                Button("Force full upload") { Task { await model.refresh(force: true) } }
                    .buttonStyle(ControlDeckButtonStyle(accent: theme.secondary))
                    .disabled(testing || model.busy || !model.settings.reportingEnabled)
                Text(model.networkStatus).font(.system(size: 10)).foregroundStyle(theme.muted).lineLimit(2)
            }
        }
        .padding(20).background(theme.panel.opacity(0.92), in: RoundedRectangle(cornerRadius: 4))
        .overlay(RoundedRectangle(cornerRadius: 4).stroke(theme.tertiary.opacity(0.18)))
    }

    private func receiverPage(_ theme: DashboardTheme) -> some View {
        ReceiverHostView(host: model.receiverHost, clientSettings: draft, bindAddress: $receiverBind,
                         port: $receiverPort, subnets: $receiverSubnets, passphrase: $receiverPassphrase)
    }

    private func footer(_ theme: DashboardTheme) -> some View {
        HStack { Spacer(); Button("Cancel") { dismiss() }.buttonStyle(ControlDeckButtonStyle(accent: theme.muted)); Button("Save changes") { save() }.buttonStyle(ControlDeckButtonStyle(accent: theme.primary)).disabled(testing || model.busy) }
            .padding(.horizontal, 28).frame(height: 78).background(theme.background.opacity(0.82))
            .overlay(alignment: .top) { Rectangle().fill(theme.primary.opacity(0.12)).frame(height: 1) }
    }

    private func save() {
        do {
            if !snapshotEnabled { draft.snapshotFolder = "" }
            try model.save(draft); dismiss()
        } catch { message = error.localizedDescription }
    }

    private func chooseFolder(_ selected: (String) -> Void) {
        let panel = NSOpenPanel(); panel.canChooseDirectories = true; panel.canChooseFiles = false
        panel.canCreateDirectories = true; panel.allowsMultipleSelection = false
        if panel.runModal() == .OK, let url = panel.url { selected(url.path) }
    }
}

private struct ReceiverHostView: View {
    @ObservedObject var host: ReceiverHostModel
    let clientSettings: UsageCore.Settings
    @Binding var bindAddress: String
    @Binding var port: String
    @Binding var subnets: String
    @Binding var passphrase: String
    @Environment(\.dashboardTheme) private var theme

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 10) {
                    Circle().fill(host.healthy ? theme.primary : theme.muted).frame(width: 9, height: 9)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(host.running ? "RECEIVER HOST ACTIVE" : "RECEIVER HOST").font(.system(size: 10, weight: .bold)).tracking(0.8)
                        Text(host.runtimeMessage).font(.system(size: 11)).foregroundStyle(theme.muted)
                    }
                    Spacer()
                    Link("Get .NET 10", destination: URL(string: "https://dotnet.microsoft.com/download/dotnet/10.0")!)
                        .font(.system(size: 11, weight: .semibold)).foregroundStyle(theme.secondary)
                    Button("Check") { host.refreshInstallation(); Task { await host.checkHealth() } }.buttonStyle(ControlDeckButtonStyle(accent: theme.muted))
                }.padding(16).background(theme.panel.opacity(0.92), in: RoundedRectangle(cornerRadius: 4))

                HStack(spacing: 9) {
                    Button("Initialize receiver") {
                        host.initializeReceiver(); syncFields()
                    }.buttonStyle(ControlDeckButtonStyle(accent: theme.secondary))
                    Button(host.running ? "Running" : "Start receiver") { Task { await host.start() } }
                        .buttonStyle(ControlDeckButtonStyle(accent: theme.primary)).disabled(host.running)
                    Button("Stop") { host.stop() }.buttonStyle(ControlDeckButtonStyle(accent: theme.muted)).disabled(!host.running)
                    Spacer()
                    Text("\(host.clientCount) enabled client\(host.clientCount == 1 ? "" : "s")").font(.system(size: 10)).foregroundStyle(theme.tertiary)
                }

                VStack(spacing: 6) {
                    NetworkRow("BIND ADDRESS") { TextField("127.0.0.1", text: $bindAddress).textFieldStyle(ControlDeckFieldStyle()) }
                    NetworkRow("PORT") { TextField("4747", text: $port).textFieldStyle(ControlDeckFieldStyle()).frame(width: 110) }
                    NetworkRow("ALLOWED SUBNETS") {
                        TextField("192.168.1.0/24, 127.0.0.0/8", text: $subnets).textFieldStyle(ControlDeckFieldStyle())
                    }
                    HStack {
                        Text("Use explicit LAN CIDRs. Unrestricted /0 ranges are rejected.").font(.system(size: 10)).foregroundStyle(theme.muted)
                        Spacer()
                        Button("Save host settings") { host.saveConfiguration(bindAddress: bindAddress, portText: port, subnetsText: subnets) }
                            .buttonStyle(ControlDeckButtonStyle(accent: theme.secondary))
                    }
                }.padding(16).background(theme.panel.opacity(0.92), in: RoundedRectangle(cornerRadius: 4))
                    .overlay(RoundedRectangle(cornerRadius: 4).stroke(theme.tertiary.opacity(0.18)))

                VStack(alignment: .leading, spacing: 9) {
                    HStack {
                        Text("LOCAL RECEIVER CLIENTS").font(.system(size: 10, weight: .bold)).tracking(0.7).foregroundStyle(theme.tertiary)
                        Spacer()
                        Text("Select clients to rotate together").font(.system(size: 10)).foregroundStyle(theme.muted)
                    }
                    if host.configuration.clients.isEmpty {
                        Text("No clients registered. Add this Mac below, then use the same shared passphrase in Network Reporting.")
                            .font(.system(size: 11)).foregroundStyle(theme.muted).padding(.vertical, 5)
                    } else {
                        ForEach(host.configuration.clients) { client in
                            HStack(spacing: 10) {
                                Toggle("Select \(client.machineName)", isOn: Binding(
                                    get: { host.selectedClientIds.contains(client.clientId) },
                                    set: { selected in
                                        if selected { host.selectedClientIds.insert(client.clientId) }
                                        else { host.selectedClientIds.remove(client.clientId) }
                                    })).labelsHidden().toggleStyle(.checkbox).tint(theme.primary)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(client.machineName).font(.system(size: 11, weight: .semibold))
                                    Text(client.clientId).font(.system(size: 9, design: .monospaced)).foregroundStyle(theme.muted).textSelection(.enabled)
                                }
                                Spacer()
                                Toggle("Enabled", isOn: Binding(get: { client.enabled }, set: { host.setEnabled($0, clientId: client.clientId) }))
                                    .toggleStyle(.switch).tint(theme.primary).font(.system(size: 10)).fixedSize()
                            }.padding(.horizontal, 10).frame(height: 43).background(theme.background.opacity(0.45), in: RoundedRectangle(cornerRadius: 4))
                        }
                    }
                    HStack(spacing: 10) {
                        SecureField("New shared passphrase", text: $passphrase).textFieldStyle(ControlDeckFieldStyle())
                        Button("Add this Mac") {
                            let secret = passphrase; passphrase = ""
                            host.register(clientId: clientSettings.clientId, machineName: clientSettings.machineName, passphrase: secret)
                        }.buttonStyle(ControlDeckButtonStyle(accent: theme.secondary))
                        Button("Rotate selected") {
                            let secret = passphrase; passphrase = ""; host.rotateSelected(passphrase: secret)
                        }.buttonStyle(ControlDeckButtonStyle(accent: theme.primary)).disabled(host.selectedClientIds.isEmpty)
                    }
                    Text("After adding or rotating, enter the matching passphrase in each selected client app and test its encrypted connection. Stored salts and derived keys are never displayed or served over the network.")
                        .font(.system(size: 10)).foregroundStyle(theme.muted).fixedSize(horizontal: false, vertical: true)
                }.padding(16).background(theme.panel.opacity(0.92), in: RoundedRectangle(cornerRadius: 4))
                    .overlay(RoundedRectangle(cornerRadius: 4).stroke(theme.primary.opacity(0.12)))

                Text(host.statusMessage).font(.system(size: 11)).foregroundStyle(host.healthy ? theme.primary : theme.secondary).textSelection(.enabled)
            }
        }
        .task { host.refreshInstallation(); syncFields(); await host.checkHealth() }
    }

    private func syncFields() {
        bindAddress = host.configuration.bindAddress; port = String(host.configuration.port)
        subnets = host.configuration.allowedSubnets.joined(separator: "\n")
    }
}

private struct SettingCard<Content: View>: View {
    @Environment(\.dashboardTheme) private var theme
    let title: String
    let description: String
    var tall = false
    @ViewBuilder let content: Content
    init(title: String, description: String, tall: Bool = false, @ViewBuilder content: () -> Content) { self.title = title; self.description = description; self.tall = tall; self.content = content() }
    var body: some View {
        Group {
            if tall {
                VStack(alignment: .leading, spacing: 7) {
                    Text(title).font(.system(size: 10, weight: .bold)).tracking(0.7).foregroundStyle(theme.tertiary)
                    Text(description).font(.system(size: 11)).foregroundStyle(theme.muted).lineLimit(1)
                    content.frame(maxWidth: .infinity, alignment: .leading)
                }
            } else {
                HStack(spacing: 20) {
                    VStack(alignment: .leading, spacing: 7) {
                        Text(title).font(.system(size: 10, weight: .bold)).tracking(0.7).foregroundStyle(theme.tertiary)
                        Text(description).font(.system(size: 11)).foregroundStyle(theme.muted).lineLimit(1)
                    }.frame(maxWidth: .infinity, alignment: .leading)
                    content.frame(width: 190, alignment: .trailing)
                }
            }
        }.padding(.horizontal, 20).frame(height: tall ? 126 : 78)
            .background(theme.panel.opacity(0.92), in: RoundedRectangle(cornerRadius: 4))
            .overlay(RoundedRectangle(cornerRadius: 4).stroke(theme.primary.opacity(0.1)))
    }
}

private struct NetworkRow<Content: View>: View {
    let label: String
    @ViewBuilder let content: Content
    init(_ label: String, @ViewBuilder content: () -> Content) { self.label = label; self.content = content() }
    var body: some View { HStack { Text(label).font(.system(size: 10, weight: .bold)).foregroundStyle(.secondary).frame(width: 215, alignment: .leading); content.frame(maxWidth: .infinity, alignment: .leading) }.frame(height: 30) }
}

private struct ControlDeckButtonStyle: ButtonStyle {
    @Environment(\.dashboardTheme) private var theme
    let accent: Color
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.font(.system(size: 12, weight: .semibold)).foregroundStyle(theme.text).padding(.horizontal, 16).frame(height: 38)
            .background(theme.panel, in: RoundedRectangle(cornerRadius: 6)).overlay(RoundedRectangle(cornerRadius: 6).stroke(accent.opacity(0.8)))
            .shadow(color: accent.opacity(configuration.isPressed ? 0.15 : 0.3), radius: 6)
    }
}

private struct ControlDeckFieldStyle: TextFieldStyle {
    @Environment(\.dashboardTheme) private var theme
    func _body(configuration: TextField<Self._Label>) -> some View {
        configuration.font(.system(size: 11, design: .monospaced)).padding(.horizontal, 9).frame(height: 28)
            .background(theme.background.opacity(0.7), in: RoundedRectangle(cornerRadius: 5))
            .overlay(RoundedRectangle(cornerRadius: 5).stroke(theme.primary.opacity(0.55))).shadow(color: theme.primary.opacity(0.16), radius: 4)
    }
}

private extension View {
    func controlDeckInput() -> some View {
        modifier(ControlDeckInputModifier())
    }
}

private struct ControlDeckInputModifier: ViewModifier {
    @Environment(\.dashboardTheme) private var theme
    func body(content: Content) -> some View {
        content.padding(.horizontal, 9).frame(height: 30)
            .background(theme.background.opacity(0.72), in: RoundedRectangle(cornerRadius: 5))
            .overlay(RoundedRectangle(cornerRadius: 5).stroke(theme.primary.opacity(0.58)))
            .shadow(color: theme.primary.opacity(0.14), radius: 4)
    }
}
