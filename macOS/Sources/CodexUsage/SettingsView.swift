import SwiftUI
import AppKit
import UsageCore

struct SettingsView: View {
    @ObservedObject var model: DashboardModel
    @Environment(\.dismiss) private var dismiss
    @State private var draft: UsageCore.Settings
    @State private var passphrase = ""
    @State private var message = ""
    @State private var testing = false
    init(model: DashboardModel) {
        self.model = model; _draft = State(initialValue: model.settings)
    }
    var body: some View {
        VStack(spacing: 0) {
            Text("Settings").font(.title2.bold()).frame(maxWidth: .infinity, alignment: .leading).padding(24)
            TabView {
                Form {
                    Section("Codex data") {
                        Text(draft.sessionsFolder).textSelection(.enabled).font(.caption)
                        Button("Choose session folder…") { chooseFolder { draft.sessionsFolder = $0 } }
                    }
                    Section("Display") {
                        Picker("Refresh", selection: $draft.refreshSeconds) {
                            Text("15 seconds").tag(15); Text("30 seconds").tag(30)
                            Text("1 minute").tag(60); Text("5 minutes").tag(300)
                        }
                        Picker("Appearance", selection: $draft.appearance) {
                            Text("System").tag("system"); Text("Light").tag("light"); Text("Dark").tag("dark")
                        }
                    }
                    Text("Logs stay on this Mac. Local history stores only derived usage points. Token counts describe logged activity, not billing or plan limits.")
                        .font(.caption).foregroundStyle(.secondary)
                }.formStyle(.grouped).tabItem { Label("General", systemImage: "slider.horizontal.3") }
                Form {
                    Section("Receiver") {
                        TextField("Address", text: $draft.receiverURL)
                        TextField("Machine name", text: $draft.machineName)
                        LabeledContent("Client ID") {
                            Text(draft.clientId).font(.caption.monospaced()).textSelection(.enabled)
                            Button { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(draft.clientId, forType: .string) }
                                label: { Image(systemName: "doc.on.doc") }.help("Copy client ID")
                        }
                        Text("Register this client ID on the receiver, then enter the shared passphrase.").font(.caption).foregroundStyle(.secondary)
                        SecureField("Passphrase", text: $passphrase)
                        Button(testing ? "Testing…" : "Pair / Test encrypted connection") {
                            testing = true; message = ""
                            let secret = passphrase; passphrase = ""
                            Task {
                                do { try await model.pairAndTest(draft, passphrase: secret); message = "Encrypted connection verified. Derived key saved in Keychain." }
                                catch { message = error.localizedDescription }
                                testing = false
                            }
                        }.disabled(testing)
                    }
                    Section("Reporting") {
                        Toggle("Enable LAN reporting", isOn: $draft.reportingEnabled)
                        Picker("Project detail", selection: $draft.projectPrivacy) {
                            Text("Anonymous project IDs").tag(ProjectPrivacy.anonymous)
                            Text("No project breakdown").tag(ProjectPrivacy.none)
                            Text("Project names").tag(ProjectPrivacy.names)
                        }
                        Text("Only hourly aggregates leave this Mac. Prompts, responses, session IDs and paths are never sent. Use your trusted LAN receiver.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }.formStyle(.grouped).tabItem { Label("Network", systemImage: "network") }
                Form {
                    Section("Snapshot export") {
                        Text(draft.snapshotFolder.isEmpty ? "Disabled" : draft.snapshotFolder).font(.caption).textSelection(.enabled)
                        Button("Choose destination…") { chooseFolder { draft.snapshotFolder = $0 } }
                        Button("Disable export") { draft.snapshotFolder = "" }.disabled(draft.snapshotFolder.isEmpty)
                        Picker("Capture every", selection: $draft.snapshotMinutes) {
                            ForEach([5, 15, 30, 60], id: \.self) { Text("\($0) minutes").tag($0) }
                        }
                        Toggle("Hide project names", isOn: $draft.hideSnapshotProjects)
                        Text("Replaces codex-usage-latest.png in the selected folder. A synced folder may share the image through your chosen sync service.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }.formStyle(.grouped).tabItem { Label("Export", systemImage: "photo") }
            }
            if !message.isEmpty { Text(message).font(.caption).textSelection(.enabled).padding(.horizontal, 24).padding(.top, 12) }
            HStack {
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Spacer()
                Button("Save") {
                    do { try model.save(draft); dismiss() } catch { message = error.localizedDescription }
                }.keyboardShortcut(.defaultAction).disabled(testing || model.busy)
            }.padding(24)
        }.frame(width: 650, height: 640)
    }
    private func chooseFolder(_ selected: (String) -> Void) {
        let panel = NSOpenPanel(); panel.canChooseDirectories = true; panel.canChooseFiles = false
        panel.canCreateDirectories = true; panel.allowsMultipleSelection = false
        if panel.runModal() == .OK, let url = panel.url { selected(url.path) }
    }
}
