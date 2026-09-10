import SwiftUI
import Charts
import UsageCore

func count(_ value: Int64) -> String { value.formatted(.number.notation(.compactName)) }
struct Metric: View {
    let title: String
    let value: Int64
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.subheadline).foregroundStyle(.secondary)
            Text(count(value)).font(.system(size: 28, weight: .semibold, design: .rounded)).monospacedDigit()
        }.frame(maxWidth: .infinity, alignment: .leading).padding(18)
            .background(.quaternary.opacity(0.4), in: RoundedRectangle(cornerRadius: 14))
            .help(value.formatted())
    }
}
struct DailyValue: Identifiable {
    var id: Date { day }
    let day: Date
    let total: Int64
}
func dailyValues(_ points: [UsagePoint]) -> [DailyValue] {
    let cal = Calendar.current
    let grouped = Dictionary(grouping: points) { cal.startOfDay(for: $0.time) }
    return (-29...0).map {
        let day = cal.date(byAdding: .day, value: $0, to: cal.startOfDay(for: Date()))!
        return DailyValue(day: day, total: (grouped[day] ?? []).reduce(0) { $0 + $1.tokens.total })
    }
}
struct HourValue: Identifiable {
    var id: String { "\(hour)-\(model)" }
    let hour: Int
    let model: String
    let total: Int64
}
func hourValues(_ points: [UsagePoint]) -> [HourValue] {
    var values: [String: HourValue] = [:]
    for p in points {
        let h = Calendar.current.component(.hour, from: p.time)
        let id = "\(h)-\(p.model)"
        values[id] = HourValue(hour: h, model: p.model, total: (values[id]?.total ?? 0) + p.tokens.total)
    }
    return values.values.sorted { ($0.hour, $0.model) < ($1.hour, $1.model) }
}
struct DashboardView: View {
    @ObservedObject var model: DashboardModel
    @State private var showSettings = false
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                HStack {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Codex Usage").font(.largeTitle.bold())
                        Text("Local activity, clearly counted.").foregroundStyle(.secondary)
                    }
                    Spacer()
                    if model.busy { ProgressView().controlSize(.small) }
                    Button("Rebuild 30 days") { Task { await model.refresh(force: true) } }.disabled(model.busy)
                    Button { Task { await model.refresh() } } label: { Image(systemName: "arrow.clockwise") }.help("Refresh")
                    Button { showSettings = true } label: { Image(systemName: "gearshape") }.help("Settings")
                }
                today
                history
                HStack {
                    Text(model.selectedDay.formatted(date: .complete, time: .omitted)).font(.title2.bold())
                    Spacer()
                    Text("\(count(model.selectedPoints.reduce(0) { $0 + $1.tokens.total })) tokens").foregroundStyle(.secondary)
                    Button("Today") { model.selectedDate = nil; model.selectedHour = nil }
                }
                HStack(alignment: .top, spacing: 24) {
                    hourly.frame(maxWidth: .infinity)
                    projects.frame(width: 290)
                }
                Divider()
                HStack {
                    VStack(alignment: .leading, spacing: 5) {
                        Text(model.status)
                        Text(model.networkStatus)
                    }
                    Spacer()
                    if let date = model.lastRefresh { Text("Updated \(date.formatted(date: .omitted, time: .standard))") }
                }.font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
            }.padding(28)
        }.frame(minWidth: 900, minHeight: 720)
            .sheet(isPresented: $showSettings) { SettingsView(model: model) }
            .onChange(of: model.selectedDate) { model.selectedHour = nil }
    }
    var today: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(alignment: .firstTextBaseline, spacing: 12) {
                Text("TODAY").font(.caption.bold()).foregroundStyle(.secondary)
                Text(model.todayTokens.total.formatted()).font(.system(size: 42, weight: .bold, design: .rounded)).monospacedDigit()
                Text("tokens").foregroundStyle(.secondary)
                Spacer()
                if let combined = model.visibleCombined {
                    VStack(alignment: .trailing) {
                        Text("All machines · \(count(combined.total))").font(.headline)
                        Text("Last successful sync").font(.caption).foregroundStyle(.secondary)
                    }.help(model.network.machines.sorted { $0.key < $1.key }.map { "\($0.key): \($0.value.total.formatted())" }.joined(separator: "\n"))
                }
            }
            HStack(spacing: 12) {
                Metric(title: "Input", value: model.todayTokens.input)
                Metric(title: "Cached input", value: model.todayTokens.cachedInput)
                Metric(title: "Output", value: model.todayTokens.output)
                Metric(title: "Reasoning", value: model.todayTokens.reasoning)
            }
            Text("Total = input + output. Cached input and reasoning are included in those totals.").font(.caption).foregroundStyle(.secondary)
        }
    }
    var history: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack { Text("30-day history").font(.headline); Spacer(); Text("Select a day to explore").font(.caption).foregroundStyle(.secondary) }
            Chart(dailyValues(model.points)) { v in
                BarMark(x: .value("Day", v.day, unit: .day), y: .value("Tokens", v.total))
                    .foregroundStyle(Calendar.current.isDate(v.day, inSameDayAs: model.selectedDay) ? Color.teal : Color.accentColor.opacity(0.55))
                    .annotation(position: .overlay) { EmptyView() }
            }.chartXSelection(value: $model.selectedDate)
                .chartGesture { proxy in SpatialTapGesture().onEnded { proxy.selectXValue(at: $0.location.x) } }
                .chartYAxis { AxisMarks { AxisGridLine(); AxisValueLabel(format: FloatingPointFormatStyle<Double>.number.notation(.compactName)) } }
                .frame(height: 150)
        }.padding(20).background(.quaternary.opacity(0.2), in: RoundedRectangle(cornerRadius: 16))
    }
    var hourly: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Text("Hourly usage by model").font(.headline)
                Spacer()
                if let hour = model.selectedHour { Button("Clear \(hour):00") { model.selectedHour = nil }.font(.caption) }
            }
            Chart(hourValues(model.selectedPoints)) { v in
                BarMark(x: .value("Hour", v.hour), y: .value("Tokens", v.total))
                    .foregroundStyle(by: .value("Model", v.model))
                if let hour = model.selectedHour, hour == v.hour {
                    RuleMark(x: .value("Selected hour", hour)).foregroundStyle(.secondary)
                }
            }.chartXScale(domain: -1...24).chartXSelection(value: $model.selectedHour)
                .chartGesture { proxy in SpatialTapGesture().onEnded { proxy.selectXValue(at: $0.location.x) } }
                .chartYAxis { AxisMarks { AxisGridLine(); AxisValueLabel(format: FloatingPointFormatStyle<Double>.number.notation(.compactName)) } }
                .chartXAxis { AxisMarks(values: [0, 4, 8, 12, 16, 20, 23]) }.frame(height: 210)
            if model.selectedPoints.isEmpty { Text("No logged activity for this day.").foregroundStyle(.secondary) }
        }
    }
    var projects: some View {
        ProjectList(points: model.selectedPoints.filter { model.selectedHour == nil || Calendar.current.component(.hour, from: $0.time) == model.selectedHour }, hideNames: false)
    }
}
struct ProjectList: View {
    let points: [UsagePoint]
    let hideNames: Bool
    var entries: [(String, Int64)] {
        Dictionary(grouping: points, by: \.project).map { ($0.key, $0.value.reduce(0) { $0 + $1.tokens.total }) }
            .sorted { $0.1 == $1.1 ? $0.0 < $1.0 : $0.1 > $1.1 }
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Usage by project").font(.headline)
            if entries.isEmpty { Text("No activity").foregroundStyle(.secondary) }
            ForEach(Array(entries.prefix(10).enumerated()), id: \.offset) { i, item in
                VStack(spacing: 5) {
                    HStack {
                        Text(hideNames ? "Project \(i + 1)" : item.0).lineLimit(1)
                        Spacer(); Text(count(item.1)).monospacedDigit().foregroundStyle(.secondary)
                    }.font(.caption).help(hideNames ? item.1.formatted() : "\(item.0): \(item.1.formatted())")
                    GeometryReader { geometry in
                        Capsule().fill(Color.teal.opacity(0.15))
                        Capsule().fill(Color.teal).frame(width: geometry.size.width * Double(item.1) / Double(max(1, entries.first?.1 ?? 1)))
                    }.frame(height: 5)
                }
            }
            if entries.count > 10 { Text("\(entries.count - 10) more projects · \(count(entries.dropFirst(10).reduce(0) { $0 + $1.1 })) tokens").font(.caption).foregroundStyle(.secondary) }
        }
    }
}
struct SnapshotView: View {
    let points: [UsagePoint]
    let total: Tokens
    let hideProjects: Bool
    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            Text("Codex Usage · \(Date().formatted(date: .abbreviated, time: .shortened))").font(.title.bold())
            Text("Today · \(total.total.formatted()) tokens").font(.largeTitle)
            HStack {
                Metric(title: "Input", value: total.input); Metric(title: "Cached input", value: total.cachedInput)
                Metric(title: "Output", value: total.output); Metric(title: "Reasoning", value: total.reasoning)
            }
            Chart(dailyValues(points)) { v in
                BarMark(x: .value("Day", v.day, unit: .day), y: .value("Tokens", v.total)).foregroundStyle(.teal)
            }.chartYAxis { AxisMarks { AxisGridLine(); AxisValueLabel(format: FloatingPointFormatStyle<Double>.number.notation(.compactName)) } }
                .frame(height: 200)
            ProjectList(points: points.filter { Calendar.current.isDateInToday($0.time) }, hideNames: hideProjects)
        }
    }
}
