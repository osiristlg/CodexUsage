import SwiftUI

struct DashboardTheme: Identifiable, Hashable {
    let id: String
    let background: Color
    let panel: Color
    let text: Color
    let muted: Color
    let primary: Color
    let secondary: Color
    let tertiary: Color
    let series: [Color]

    static let all: [DashboardTheme] = [
        .init(id: "Night City", background: .rgb(9, 5, 24), panel: .rgb(21, 16, 45), text: .rgb(244, 246, 255), muted: .rgb(143, 151, 177), primary: .rgb(0, 218, 255), secondary: .rgb(255, 48, 190), tertiary: .rgb(255, 174, 42), series: [.rgb(134, 99, 255), .rgb(29, 211, 176), .rgb(255, 168, 76), .rgb(80, 156, 255), .rgb(244, 101, 153), .rgb(180, 188, 212)]),
        .init(id: "Neon Sunset", background: .rgb(25, 9, 35), panel: .rgb(40, 16, 47), text: .rgb(255, 242, 223), muted: .rgb(185, 143, 157), primary: .rgb(255, 182, 39), secondary: .rgb(255, 77, 109), tertiary: .rgb(198, 78, 255), series: [.rgb(255, 182, 39), .rgb(255, 77, 109), .rgb(255, 119, 48), .rgb(198, 78, 255), .rgb(255, 217, 120), .rgb(222, 129, 186)]),
        .init(id: "Toxic Rain", background: .rgb(6, 21, 15), panel: .rgb(11, 33, 25), text: .rgb(234, 255, 216), muted: .rgb(126, 165, 139), primary: .rgb(182, 255, 46), secondary: .rgb(0, 255, 200), tertiary: .rgb(255, 214, 62), series: [.rgb(182, 255, 46), .rgb(0, 255, 200), .rgb(87, 219, 69), .rgb(255, 214, 62), .rgb(48, 210, 255), .rgb(173, 205, 126)]),
        .init(id: "Ion Storm", background: .rgb(7, 14, 40), panel: .rgb(16, 26, 62), text: .rgb(236, 243, 255), muted: .rgb(139, 153, 190), primary: .rgb(72, 229, 255), secondary: .rgb(157, 124, 255), tertiary: .rgb(89, 124, 255), series: [.rgb(72, 229, 255), .rgb(157, 124, 255), .rgb(89, 124, 255), .rgb(92, 180, 255), .rgb(206, 117, 255), .rgb(171, 196, 233)]),
        .init(id: "Redline District", background: .rgb(22, 7, 7), panel: .rgb(40, 16, 14), text: .rgb(255, 240, 223), muted: .rgb(181, 137, 124), primary: .rgb(255, 59, 48), secondary: .rgb(255, 159, 28), tertiary: .rgb(0, 205, 255), series: [.rgb(255, 59, 48), .rgb(255, 159, 28), .rgb(255, 91, 75), .rgb(255, 205, 54), .rgb(0, 205, 255), .rgb(218, 153, 126)])
    ]

    static func resolve(_ name: String) -> DashboardTheme { all.first { $0.id == name } ?? all[0] }
}

private extension Color {
    static func rgb(_ red: Double, _ green: Double, _ blue: Double) -> Color {
        Color(red: red / 255, green: green / 255, blue: blue / 255)
    }
}

private struct DashboardThemeKey: EnvironmentKey { static let defaultValue = DashboardTheme.all[0] }
extension EnvironmentValues {
    var dashboardTheme: DashboardTheme {
        get { self[DashboardThemeKey.self] }
        set { self[DashboardThemeKey.self] = newValue }
    }
}

struct NeonPanel: ViewModifier {
    @Environment(\.dashboardTheme) private var theme
    let accent: Color
    func body(content: Content) -> some View {
        content
            .background(theme.panel.opacity(0.96), in: RoundedRectangle(cornerRadius: 22, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 22, style: .continuous).stroke(accent.opacity(0.2), lineWidth: 1))
            .shadow(color: accent.opacity(0.07), radius: 16)
    }
}

extension View {
    func neonPanel(_ accent: Color) -> some View { modifier(NeonPanel(accent: accent)) }
}
