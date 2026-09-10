import SwiftUI
import AppKit

@main struct CodexUsageApp: App {
    @StateObject private var model = DashboardModel()
    var body: some Scene {
        Window("Codex Usage", id: "dashboard") {
            DashboardView(model: model)
                .preferredColorScheme(model.settings.appearance == "system" ? nil : model.settings.appearance == "dark" ? .dark : .light)
                .task { await model.run() }
        }
        .defaultSize(width: 1160, height: 860)
        .commands {
            CommandGroup(after: .appInfo) {
                Button("Refresh") { Task { await model.refresh() } }.keyboardShortcut("r")
                Button("Rebuild 30 days") { Task { await model.refresh(force: true) } }.disabled(model.busy)
            }
        }
        Settings { SettingsView(model: model) }
    }
}
