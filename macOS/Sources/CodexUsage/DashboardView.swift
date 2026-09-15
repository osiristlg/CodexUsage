import SwiftUI
import Charts
import UsageCore
import UsagePresentation

func count(_ value: Int64) -> String {
    if value >= 1_000_000_000 { return (Double(value) / 1_000_000_000).formatted(.number.precision(.fractionLength(0...2))) + "B" }
    if value >= 1_000_000 { return (Double(value) / 1_000_000).formatted(.number.precision(.fractionLength(0...2))) + "M" }
    if value >= 1_000 { return (Double(value) / 1_000).formatted(.number.precision(.fractionLength(0...1))) + "K" }
    return value.formatted()
}

private func hourRange(_ hour: Int) -> String {
    let start = Calendar.current.date(from: DateComponents(hour: hour))?.formatted(date: .omitted, time: .shortened) ?? "\(hour):00"
    let end = Calendar.current.date(from: DateComponents(hour: (hour + 1) % 24))?.formatted(date: .omitted, time: .shortened) ?? "\(hour + 1):00"
    return "\(start) – \(end)"
}

private func shortHour(_ hour: Int) -> String {
    hour == 0 ? "12a" : hour < 12 ? "\(hour)a" : hour == 12 ? "12p" : "\(hour - 12)p"
}

private struct NeonButtonStyle: ButtonStyle {
    @Environment(\.dashboardTheme) private var theme
    let accent: Color
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 12, weight: .semibold))
            .foregroundStyle(theme.text)
            .padding(.horizontal, 15).frame(height: 36)
            .background(theme.panel.opacity(configuration.isPressed ? 0.72 : 1), in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(accent.opacity(configuration.isPressed ? 1 : 0.72), lineWidth: 1))
            .shadow(color: accent.opacity(configuration.isPressed ? 0.15 : 0.3), radius: configuration.isPressed ? 3 : 7)
    }
}

private struct DashboardBackdrop: View {
    @Environment(\.dashboardTheme) private var theme
    var body: some View {
        ZStack {
            LinearGradient(colors: [theme.background.opacity(0.94), theme.background], startPoint: .topLeading, endPoint: .bottomTrailing)
            Circle().fill(theme.primary.opacity(0.08)).frame(width: 560).blur(radius: 90).offset(x: -430, y: -330)
            Circle().fill(theme.secondary.opacity(0.07)).frame(width: 520).blur(radius: 100).offset(x: 460, y: 360)
            Circle().fill(theme.tertiary.opacity(0.035)).frame(width: 340).blur(radius: 70).offset(x: 400, y: -250)
        }.ignoresSafeArea()
    }
}

struct DashboardView: View {
    @ObservedObject var model: DashboardModel
    @Environment(\.dashboardTheme) private var theme
    @State private var showSettings = false

    var body: some View {
        ZStack {
            DashboardBackdrop()
            VStack(spacing: 0) {
                header
                GeometryReader { geometry in
                    let margin = max(28.0, geometry.size.width / 35)
                    let width = max(820, geometry.size.width - margin * 2)
                    let chartHeight = max(220, (geometry.size.height - 253) / 2)
                    ScrollView([.vertical, .horizontal]) {
                        VStack(spacing: 19) {
                            HeroPanel(model: model).frame(width: width, height: 195)
                            HStack(spacing: 19) {
                                HourlyPanel(model: model).frame(width: (width - 19) * 0.66, height: chartHeight)
                                ProjectPanel(model: model).frame(width: (width - 19) * 0.34, height: chartHeight)
                            }
                            HistoryPanel(model: model).frame(width: width, height: chartHeight)
                        }
                        .padding(.horizontal, margin).padding(.top, 16).padding(.bottom, 28)
                    }.scrollIndicators(.never)
                }
            }
        }
        .frame(minWidth: 920, minHeight: 700)
        .sheet(isPresented: $showSettings) { SettingsView(model: model) }
    }

    private var header: some View {
        HStack(spacing: 10) {
            Text("CODEX  /  USAGE").font(.system(size: 15, weight: .semibold, design: .rounded)).tracking(1.2)
            Spacer()
            if model.busy { ProgressView().controlSize(.small).tint(theme.primary) }
            Text(model.lastRefresh.map { "Updated \($0.formatted(date: .omitted, time: .shortened))  ·  every \(refreshLabel)" } ?? model.status)
                .font(.system(size: 11)).foregroundStyle(theme.muted).lineLimit(1).frame(maxWidth: 220, alignment: .trailing)
            Button("↻  Refresh now") { Task { await model.refresh() } }
                .buttonStyle(NeonButtonStyle(accent: theme.primary)).disabled(model.busy)
            Button("◷  Rebuild 30 days") { Task { await model.refresh(force: true) } }
                .buttonStyle(NeonButtonStyle(accent: theme.secondary)).disabled(model.busy)
            Button { showSettings = true } label: { Image(systemName: "gearshape.fill").frame(width: 17) }
                .buttonStyle(NeonButtonStyle(accent: theme.tertiary)).help("Settings")
        }
        .padding(.horizontal, 34).frame(height: 74)
        .background(theme.background.opacity(0.78)).foregroundStyle(theme.text)
    }

    private var refreshLabel: String {
        model.settings.refreshSeconds < 60 ? "\(model.settings.refreshSeconds) sec" : "\(model.settings.refreshSeconds / 60) min"
    }
}

private struct HeroPanel: View {
    @ObservedObject var model: DashboardModel
    @Environment(\.dashboardTheme) private var theme
    var body: some View {
        GeometryReader { geometry in
            HStack(alignment: .top, spacing: 28) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("TODAY’S TOKEN USAGE").font(.system(size: 11, weight: .semibold)).tracking(1.1).foregroundStyle(theme.muted)
                    Text(count(model.todayTokens.total)).font(.system(size: 49, weight: .bold, design: .rounded)).monospacedDigit()
                        .shadow(color: theme.primary.opacity(0.16), radius: 10)
                    Spacer()
                    if let combined = model.visibleCombined, let synced = model.network.lastSuccess {
                        Text("ALL MACHINES  \(count(combined.total))  ·  synced \(synced.formatted(date: .omitted, time: .shortened))")
                            .font(.system(size: 11, weight: .semibold)).tracking(0.7).foregroundStyle(theme.tertiary)
                            .help(model.network.machines.sorted { $0.key < $1.key }.map { "\($0.key): \($0.value.total.formatted())" }.joined(separator: "\n"))
                    }
                    Text("\(model.aggregates.responseCount(day: Date()).formatted()) responses across \(model.filesScanned.formatted()) log files")
                        .font(.system(size: 11, weight: .medium)).foregroundStyle(theme.muted).lineLimit(1)
                }
                .frame(width: max(270, geometry.size.width * 0.27), alignment: .leading)
                HStack(spacing: 0) {
                    HeroMetric(title: "INPUT", value: model.todayTokens.input, color: theme.series[0])
                    HeroMetric(title: "CACHED", value: model.todayTokens.cachedInput, color: theme.series[1])
                    HeroMetric(title: "OUTPUT", value: model.todayTokens.output, color: theme.series[2])
                    HeroMetric(title: "REASONING", value: model.todayTokens.reasoning, color: theme.series[3])
                }.padding(.top, 32)
            }.padding(.horizontal, 28).padding(.vertical, 24)
        }
        .foregroundStyle(theme.text).neonPanel(theme.secondary)
    }
}

private struct HeroMetric: View {
    @Environment(\.dashboardTheme) private var theme
    let title: String
    let value: Int64
    let color: Color
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 8) {
                Circle().fill(color).frame(width: 8, height: 8).shadow(color: color.opacity(0.8), radius: 5)
                Text(title).font(.system(size: 11, weight: .semibold)).tracking(0.8).foregroundStyle(theme.muted)
            }
            Text(count(value)).font(.system(size: 25, weight: .semibold, design: .rounded)).monospacedDigit().help(value.formatted())
        }.frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct HourlyPanel: View {
    @ObservedObject var model: DashboardModel
    @Environment(\.dashboardTheme) private var theme
    private var day: Date { model.selectedDate ?? Date() }
    private var values: [HourValue] { model.aggregates.hours(day: day) }
    private var models: [String] {
        Dictionary(grouping: values, by: \.model).map { ($0.key, $0.value.reduce(0) { $0 + $1.total }) }
            .sorted { $0.1 > $1.1 }.map(\.0)
    }
    private var totals: [Int64] {
        var result = Array(repeating: Int64(0), count: 24)
        for value in values { result[value.hour] += value.total }
        return result
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .firstTextBaseline) {
                Text(Calendar.current.isDateInToday(day) ? "Usage through the day" : "Usage through \(day.formatted(.dateTime.month(.abbreviated).day()))")
                    .font(.system(size: 15, weight: .semibold))
                Spacer()
                HStack(spacing: 14) {
                    ForEach(Array(models.enumerated()), id: \.element) { index, name in
                        HStack(spacing: 5) { Circle().fill(theme.series[index % theme.series.count]).frame(width: 7, height: 7); Text(name) }
                    }
                }.font(.system(size: 10)).foregroundStyle(theme.muted)
            }
            HourlyChart(model: model, values: values, models: models, totals: totals)
        }
        .padding(24).foregroundStyle(theme.text).neonPanel(theme.primary)
    }
}

private struct HourlyChart: View {
    @ObservedObject var model: DashboardModel
    @Environment(\.dashboardTheme) private var theme
    let values: [HourValue]
    let models: [String]
    let totals: [Int64]

    var body: some View {
        Chart {
            ForEach(values) { value in
                BarMark(x: .value("Hour", value.hour), y: .value("Tokens", value.total), stacking: .standard)
                    .foregroundStyle(color(for: value.model).gradient).cornerRadius(1)
            }
            if let hour = model.hoveredHour {
                RectangleMark(xStart: .value("Start", Double(hour) - 0.48), xEnd: .value("End", Double(hour) + 0.48),
                              yStart: .value("Bottom", Int64(0)), yEnd: .value("Top", max(Int64(1), totals.max() ?? 1)))
                    .foregroundStyle(theme.tertiary.opacity(0.08))
                RuleMark(x: .value("Hovered", hour)).foregroundStyle(theme.tertiary.opacity(0.8)).lineStyle(.init(lineWidth: 1))
                PointMark(x: .value("Hovered hour", hour), y: .value("Hovered total", totals[hour])).symbolSize(0)
                    .annotation(position: .top, spacing: 8, overflowResolution: .init(x: .fit, y: .disabled)) {
                        TooltipBox(accent: theme.tertiary) {
                            Text(hourRange(hour)).foregroundStyle(theme.muted)
                            Text("\(totals[hour].formatted()) tokens").font(.system(size: 12, weight: .semibold)).foregroundStyle(theme.text)
                        }
                    }
            }
        }
        .chartXScale(domain: -0.5...23.5)
        .chartXAxis { AxisMarks(values: [0, 4, 8, 12, 16, 20, 23]) { value in AxisValueLabel { if let hour = value.as(Int.self) { Text(shortHour(hour)) } }; AxisTick().foregroundStyle(.clear) } }
        .chartYAxis { AxisMarks(position: .leading, values: .automatic(desiredCount: 5)) { value in AxisGridLine().foregroundStyle(theme.muted.opacity(0.2)); AxisValueLabel { if let amount = value.as(Int64.self) { Text(count(amount)) } else if let amount = value.as(Double.self) { Text(count(Int64(amount))) } } } }
        .chartOverlay { proxy in
            GeometryReader { geometry in
                Color.clear.contentShape(Rectangle()).onContinuousHover { phase in
                    switch phase {
                    case .active(let location):
                        guard let anchor = proxy.plotFrame else { model.setHover(day: nil, hour: nil); return }
                        let plot = geometry[anchor]
                        guard plot.contains(location), let x: Double = proxy.value(atX: location.x - plot.minX) else { model.setHover(day: nil, hour: nil); return }
                        model.setHover(day: nil, hour: min(23, max(0, Int(floor(x + 0.5)))))
                    case .ended: model.setHover(day: nil, hour: nil)
                    }
                }
            }
        }
        .animation(.easeOut(duration: 0.12), value: model.hoveredHour)
    }

    private func color(for name: String) -> Color {
        theme.series[(models.firstIndex(of: name) ?? 0) % theme.series.count]
    }
}

private struct ProjectPanel: View {
    @ObservedObject var model: DashboardModel
    @Environment(\.dashboardTheme) private var theme
    private var values: [ProjectValue] {
        model.aggregates.projects(day: effectiveDay, hour: model.hoveredHour)
    }
    private var effectiveDay: Date { model.hoveredDate ?? model.selectedDate ?? Date() }
    private var scope: String {
        if let hour = model.hoveredHour { return "\(effectiveDay.formatted(.dateTime.month(.abbreviated).day())) · \(hourRange(hour))".uppercased() }
        return Calendar.current.isDateInToday(effectiveDay) ? "TODAY" : effectiveDay.formatted(.dateTime.month(.abbreviated).day()).uppercased()
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(alignment: .firstTextBaseline) {
                Text("Usage by project").font(.system(size: 15, weight: .semibold))
                Spacer(); Text(scope).font(.system(size: 9, weight: .semibold)).tracking(0.5).foregroundStyle(theme.tertiary)
            }
            if values.isEmpty {
                Spacer(); Text("No project usage for this selection").font(.system(size: 12)).foregroundStyle(theme.muted).frame(maxWidth: .infinity); Spacer()
            } else {
                VStack(spacing: 12) {
                    ForEach(Array(values.enumerated()), id: \.element.id) { index, value in
                        VStack(spacing: 7) {
                            HStack { Text(value.name).lineLimit(1); Spacer(); Text(count(value.total)).foregroundStyle(theme.muted).monospacedDigit() }
                                .font(.system(size: 11, weight: .semibold)).help("\(value.name): \(value.total.formatted())")
                            GeometryReader { geometry in
                                Capsule().fill(theme.muted.opacity(0.16))
                                Capsule().fill(theme.series[index % theme.series.count])
                                    .frame(width: geometry.size.width * Double(value.total) / Double(max(1, values.first?.total ?? 1)))
                                    .shadow(color: theme.series[index % theme.series.count].opacity(0.55), radius: 5)
                            }.frame(height: 7)
                        }
                    }
                }
                Spacer(minLength: 0)
            }
        }.padding(24).foregroundStyle(theme.text).neonPanel(theme.tertiary)
    }
}

private struct HistoryPanel: View {
    @ObservedObject var model: DashboardModel
    @Environment(\.dashboardTheme) private var theme
    private var days: [DailyValue] { model.aggregates.days }
    private var hovered: DailyValue? { model.hoveredDate.flatMap { date in days.first { Calendar.current.isDate($0.day, inSameDayAs: date) } } }
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Rolling 30-day usage").font(.system(size: 15, weight: .semibold))
                Spacer()
                if let date = model.lastRefresh { Text("Built \(date.formatted(.dateTime.month(.abbreviated).day().hour().minute()))") }
            }.foregroundStyle(theme.text)
            Chart {
                ForEach(days) { day in
                    AreaMark(x: .value("Day", day.day), y: .value("Tokens", day.total))
                        .foregroundStyle(LinearGradient(colors: [theme.primary.opacity(0.34), theme.secondary.opacity(0.015)], startPoint: .top, endPoint: .bottom))
                    LineMark(x: .value("Day", day.day), y: .value("Tokens", day.total))
                        .lineStyle(.init(lineWidth: 2.8, lineCap: .round, lineJoin: .round))
                        .foregroundStyle(theme.secondary)
                        .shadow(color: theme.primary.opacity(0.55), radius: 7)
                    PointMark(x: .value("Day", day.day), y: .value("Tokens", day.total)).symbolSize(26).foregroundStyle(theme.secondary)
                }
                if let selected = model.selectedDate, let day = days.first(where: { Calendar.current.isDate($0.day, inSameDayAs: selected) }) {
                    PointMark(x: .value("Pinned", day.day), y: .value("Tokens", day.total)).symbolSize(150)
                        .foregroundStyle(theme.tertiary)
                    PointMark(x: .value("Pinned center", day.day), y: .value("Tokens", day.total)).symbolSize(65)
                        .foregroundStyle(theme.background)
                }
                if let day = hovered {
                    RuleMark(x: .value("Hovered", day.day)).foregroundStyle(theme.tertiary.opacity(0.65))
                    PointMark(x: .value("Hover", day.day), y: .value("Tokens", day.total)).symbolSize(105).foregroundStyle(theme.secondary)
                        .annotation(position: .top, spacing: 10, overflowResolution: .init(x: .fit, y: .fit)) {
                            TooltipBox(accent: theme.tertiary) {
                                Text(day.day.formatted(.dateTime.weekday(.wide).month(.abbreviated).day())).foregroundStyle(theme.muted)
                                Text("\(day.total.formatted()) tokens").font(.system(size: 12, weight: .semibold)).foregroundStyle(theme.text)
                                ForEach(day.models.sorted(by: { $0.value > $1.value }), id: \.key) { name, value in
                                    HStack(spacing: 6) { Circle().fill(theme.series[max(0, day.models.keys.sorted().firstIndex(of: name) ?? 0) % theme.series.count]).frame(width: 6, height: 6); Text(name).lineLimit(1); Spacer(); Text(count(value)).foregroundStyle(theme.text) }
                                        .font(.system(size: 10)).foregroundStyle(theme.muted)
                                }
                                if !day.efforts.isEmpty {
                                    Divider().overlay(theme.muted.opacity(0.3))
                                    Text("REASONING EFFORT").font(.system(size: 9, weight: .semibold)).foregroundStyle(theme.tertiary)
                                    ForEach(day.efforts.sorted(by: { $0.value == $1.value ? $0.key < $1.key : $0.value > $1.value }), id: \.key) { effort, value in
                                        HStack { Text(effort); Spacer(); Text(count(value)).foregroundStyle(theme.text) }
                                            .font(.system(size: 10)).foregroundStyle(theme.muted)
                                    }
                                }
                            }.frame(width: 220)
                        }
                }
            }
            .chartXAxis { AxisMarks(values: days.enumerated().compactMap { [0, 7, 14, 21, 29].contains($0.offset) ? $0.element.day : nil }) { AxisValueLabel(format: .dateTime.month(.abbreviated).day()).foregroundStyle(theme.muted); AxisTick().foregroundStyle(.clear) } }
            .chartYAxis { AxisMarks(position: .leading, values: .automatic(desiredCount: 5)) { value in AxisGridLine().foregroundStyle(theme.muted.opacity(0.2)); AxisValueLabel { if let amount = value.as(Int64.self) { Text(count(amount)) } else if let amount = value.as(Double.self) { Text(count(Int64(amount))) } }.foregroundStyle(theme.muted) } }
            .chartOverlay { proxy in
                GeometryReader { geometry in
                    Color.clear.contentShape(Rectangle())
                        .onContinuousHover { phase in
                            switch phase {
                            case .active(let location):
                                guard let plot = proxy.plotFrame.map({ geometry[$0] }), plot.contains(location),
                                      let value: Date = proxy.value(atX: location.x - plot.minX) else { model.setHover(day: nil, hour: nil); return }
                                model.setHover(day: days.min(by: { abs($0.day.timeIntervalSince(value)) < abs($1.day.timeIntervalSince(value)) })?.day, hour: nil)
                            case .ended: model.setHover(day: nil, hour: nil)
                            }
                        }
                        .gesture(SpatialTapGesture().onEnded { event in
                            guard let plot = proxy.plotFrame.map({ geometry[$0] }), plot.contains(event.location),
                                  let value: Date = proxy.value(atX: event.location.x - plot.minX),
                                  let day = days.min(by: { abs($0.day.timeIntervalSince(value)) < abs($1.day.timeIntervalSince(value)) }) else { return }
                            model.setHover(day: day.day, hour: nil)
                            model.selectedDate = DashboardPresentation.toggledPin(current: model.selectedDate, clicked: day.day)
                        })
                }
            }
            .animation(.easeOut(duration: 0.12), value: model.hoveredDate)
        }.padding(24).foregroundStyle(theme.muted).neonPanel(theme.secondary)
    }
}

private struct TooltipBox<Content: View>: View {
    @Environment(\.dashboardTheme) private var theme
    let accent: Color
    @ViewBuilder let content: Content
    init(accent: Color, @ViewBuilder content: () -> Content) { self.accent = accent; self.content = content() }
    var body: some View {
        VStack(alignment: .leading, spacing: 4) { content }
            .padding(.horizontal, 10).padding(.vertical, 8)
            .background(theme.panel, in: RoundedRectangle(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(accent.opacity(0.65)))
            .shadow(color: accent.opacity(0.3), radius: 8)
    }
}

struct SnapshotView: View {
    let points: [UsagePoint]
    let total: Tokens
    let hideProjects: Bool
    private var projects: [ProjectValue] { DashboardPresentation.projects(points, pinnedDay: nil, hoveredDay: nil, hoveredHour: nil) }
    var body: some View {
        let theme = DashboardTheme.all[0]
        VStack(alignment: .leading, spacing: 22) {
            Text("CODEX  /  USAGE").font(.system(size: 16, weight: .semibold)).tracking(1.4)
            HStack { Text("TODAY’S TOKEN USAGE").foregroundStyle(theme.muted); Text(count(total.total)).font(.system(size: 44, weight: .bold, design: .rounded)); Spacer() }
            Chart(DashboardPresentation.days(points)) { day in
                AreaMark(x: .value("Day", day.day), y: .value("Tokens", day.total)).foregroundStyle(theme.primary.opacity(0.2))
                LineMark(x: .value("Day", day.day), y: .value("Tokens", day.total)).foregroundStyle(theme.primary).lineStyle(.init(lineWidth: 3))
            }.chartYAxis { AxisMarks { value in AxisGridLine().foregroundStyle(theme.muted.opacity(0.2)); AxisValueLabel { if let amount = value.as(Int64.self) { Text(count(amount)) } else if let amount = value.as(Double.self) { Text(count(Int64(amount))) } } } }.frame(height: 250)
            Text("Usage by project").font(.headline)
            ForEach(Array(projects.enumerated()), id: \.element.id) { index, project in
                HStack { Text(hideProjects && project.name != "Other" ? "Project \(index + 1)" : project.name); Spacer(); Text(count(project.total)) }
            }
        }.padding(30).foregroundStyle(theme.text).background(theme.background)
    }
}
