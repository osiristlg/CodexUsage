import SwiftUI
import AppKit

@main struct CodexUsageApp: App {
    @StateObject private var model = DashboardModel()
    var body: some Scene {
        Window("Codex Usage", id: "dashboard") {
            DashboardView(model: model)
                .environment(\.dashboardTheme, DashboardTheme.resolve(model.settings.appearance))
                .preferredColorScheme(.dark)
                .task { await model.run() }
        }
        .defaultSize(width: 1180, height: 900)
        .commands {
            CommandGroup(after: .appInfo) {
                Button("Refresh") { Task { await model.refresh() } }.keyboardShortcut("r")
                Button("Rebuild 30 days") { Task { await model.refresh(force: true) } }.disabled(model.busy)
            }
        }
        Settings { SettingsView(model: model) }
    }
}
