import SwiftUI
import UsagePresentation

struct ProjectTotalsView: View {
    @ObservedObject var model: DashboardModel
    @Environment(\.dashboardTheme) private var theme
    @State private var hovered: String?
    private var projects: [ProjectTotal] { model.aggregates.rollingProjects }
    private var modelNames: [String] { Array(Set(projects.flatMap { $0.models.keys })).sorted() }
    private let gold = Color(red: 1, green: 0.71, blue: 0.15)

    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            VStack(alignment: .leading, spacing: 12) {
                Text("Rolling 30-day project totals").font(.system(size: 25, weight: .semibold, design: .rounded))
                Text(model.sourceLabel).font(.system(size: 13, weight: .medium)).foregroundStyle(theme.tertiary)
                HStack(alignment: .firstTextBaseline, spacing: 18) {
                    Text(projects.reduce(Int64(0)) { $0 + $1.total }.formatted())
                        .font(.system(size: 34, weight: .bold, design: .rounded)).monospacedDigit()
                    Text("tokens · \(projects.count) projects").font(.system(size: 13)).foregroundStyle(theme.muted)
                }
            }.padding(.top, 12)
            HStack {
                Text("PROJECT").frame(maxWidth: .infinity, alignment: .leading)
                Text("TOTAL TOKENS").frame(width: 170, alignment: .trailing)
                Text("SHARE").frame(width: 80, alignment: .trailing)
            }.font(.system(size: 11, weight: .semibold)).foregroundStyle(theme.muted).padding(.horizontal, 14)
            ScrollView {
                LazyVStack(spacing: 6) {
                    ForEach(projects) { project in
                        HStack(spacing: 18) {
                            Text(project.name).lineLimit(1).frame(maxWidth: .infinity, alignment: .leading)
                            Text(project.total.formatted()).monospacedDigit().frame(width: 170, alignment: .trailing)
                            Text(project.share.formatted(.percent.precision(.fractionLength(1))))
                                .monospacedDigit().foregroundStyle(theme.muted).frame(width: 80, alignment: .trailing)
                        }.font(.system(size: 13, weight: .medium)).padding(.horizontal, 14).padding(.vertical, 14)
                            .background(hovered == project.id ? gold.opacity(0.13) : theme.panel, in: RoundedRectangle(cornerRadius: 9))
                            .overlay(RoundedRectangle(cornerRadius: 9).stroke(hovered == project.id ? gold.opacity(0.75) : theme.muted.opacity(0.12)))
                            .onHover { inside in
                                if inside { if hovered != project.id { hovered = project.id } }
                                else if hovered == project.id { hovered = nil }
                            }
                    }
                    if projects.isEmpty {
                        Text("No project usage in this 30-day window").foregroundStyle(theme.muted).padding(30)
                    }
                }.padding(1)
            }
        }.padding(28).foregroundStyle(theme.text).background(theme.background)
            .overlay(alignment: .topTrailing) {
                if let project = projects.first(where: { $0.id == hovered }) {
                    VStack(alignment: .leading, spacing: 7) {
                        Text(project.name).font(.system(size: 12, weight: .semibold)).lineLimit(2)
                        Text("\(project.total.formatted()) tokens").font(.system(size: 12, weight: .semibold))
                        Divider().overlay(gold.opacity(0.35))
                        ForEach(project.models.sorted(by: { $0.value == $1.value ? $0.key < $1.key : $0.value > $1.value }), id: \.key) { name, total in
                            HStack(spacing: 7) {
                                Circle().fill(theme.series[(modelNames.firstIndex(of: name) ?? 0) % theme.series.count]).frame(width: 7, height: 7)
                                Text(name).lineLimit(1).foregroundStyle(theme.muted)
                                Spacer(minLength: 12)
                                Text(total.formatted()).monospacedDigit()
                            }.font(.system(size: 11))
                        }
                    }.padding(12).frame(width: 280)
                        .background(theme.panel, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(gold.opacity(0.8)))
                        .shadow(color: gold.opacity(0.18), radius: 8)
                        .padding(.top, 195).padding(.trailing, 24).allowsHitTesting(false)
                }
            }
            .onChange(of: model.source) { _, _ in hovered = nil }
            .frame(minWidth: 620, minHeight: 400)
    }
}
