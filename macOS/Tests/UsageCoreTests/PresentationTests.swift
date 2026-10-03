import Foundation
import UsageCore
import UsagePresentation

struct PresentationTests {
    func run() throws {
        struct Point: Encodable {
            let time: Date
            let model = "Model"
            let project = "Project"
            let tokens: Tokens
            let effort: String?
        }
        let day = WireTime.date("2026-09-15T00:00:00Z")!
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let fixture = [
            Point(time: day, tokens: Tokens(input: 10, output: 2), effort: "Light"),
            Point(time: day, tokens: Tokens(input: 20), effort: "Medium"),
            Point(time: day, tokens: Tokens(input: 30), effort: "High"),
            Point(time: day, tokens: Tokens(input: 5), effort: "High"),
            Point(time: day, tokens: Tokens(input: 7), effort: nil),
            Point(time: day, tokens: Tokens(input: 8), effort: "Unknown"),
            Point(time: day, tokens: Tokens(input: 9), effort: "  "),
            Point(time: day.addingTimeInterval(-86400), tokens: Tokens(input: 100), effort: "High")
        ]
        let points = try JSONDecoder().decode([UsagePoint].self, from: JSONEncoder().encode(fixture))
        let days = DashboardPresentation.days(points, now: day, calendar: calendar)
        let today = days.last!
        expectEqual(today.efforts, ["Light": 12, "Medium": 20, "High": 35, "Unknown": 24])
        expectEqual(today.total, 91)
        expectEqual(today.models, ["Model": 91])
        expectEqual(today.efforts.values.reduce(0, +), today.total)
        expectEqual(days[28].efforts, ["High": 100])
        expectEqual(days[0].efforts, [:])
        let hours = DashboardPresentation.hours(points, day: day, calendar: calendar)
        expectEqual(hours.count, 1)
        expectEqual(hours[0].total, today.total)
        var mixed = points
        for index in mixed.indices {
            mixed[index].time = day.addingTimeInterval(Double(index) * 3600)
            mixed[index].model = "Model \(index % 3)"
            mixed[index].project = "Project \(index)"
        }
        let mixedCache = DashboardAggregates(mixed, now: day, calendar: calendar)
        expectEqual(mixedCache.responseCount(day: day), mixed.count)
        expectEqual(mixedCache.hours(day: day), DashboardPresentation.hours(mixed, day: day, calendar: calendar))
        for hour in [nil, 0, 1, 7, 23] as [Int?] {
            expectEqual(mixedCache.projects(day: day, hour: hour), DashboardPresentation.projects(mixed, pinnedDay: day, hoveredDay: nil, hoveredHour: hour, calendar: calendar))
        }
        let cached = DashboardAggregates(points, now: day, calendar: calendar)
        expectEqual(cached.days, days)
        expectEqual(cached.efforts(day: day, hour: 0), today.efforts)
        expectEqual(cached.efforts(day: day, hour: 1), [:])
        expectEqual(cached.efforts(day: day.addingTimeInterval(-86400), hour: 0), ["High": 100])
        expectEqual(mixedCache.efforts(day: day, hour: 0), ["Light": 12])
        expectEqual(mixedCache.efforts(day: day, hour: 4), ["Unknown": 7])
        expectEqual(mixedCache.efforts(day: day, hour: 6), ["Unknown": 9])
        for hour in 0..<24 {
            expectEqual(mixedCache.efforts(day: day, hour: hour).values.reduce(0, +), mixedCache.hours(day: day).filter { $0.hour == hour }.reduce(Int64(0)) { $0 + $1.total })
        }
        for selected in [day, day.addingTimeInterval(-86400), day.addingTimeInterval(86400)] {
            expectEqual(cached.hours(day: selected), DashboardPresentation.hours(points, day: selected, calendar: calendar))
            let expected = points.filter { calendar.isDate($0.time, inSameDayAs: selected) }.reduce(Tokens()) { $0 + $1.tokens }
            expectEqual(cached.tokens(day: selected), expected)
            for hour in [nil, 0, 12, 23] as [Int?] {
                expectEqual(cached.projects(day: selected, hour: hour), DashboardPresentation.projects(points, pinnedDay: selected, hoveredDay: nil, hoveredHour: hour, calendar: calendar))
            }
        }
        // A new refresh replaces, rather than accumulates, every derived bucket.
        let refreshed = DashboardAggregates(Array(points.prefix(1)), now: day.addingTimeInterval(86400), calendar: calendar)
        expectEqual(refreshed.efforts(day: day, hour: 0), ["Light": 12])
        expectEqual(refreshed.tokens(day: day).total, 12)
        expectEqual(refreshed.tokens(day: day.addingTimeInterval(-86400)).total, 0)
        expectEqual(refreshed.days.last?.total, 0)
        expectEqual(refreshed.days[28].total, 12)
        var changedCalendar = calendar
        changedCalendar.timeZone = TimeZone(secondsFromGMT: -14400)!
        let reconfigured = DashboardAggregates(points, now: day, calendar: changedCalendar)
        expectEqual(reconfigured.days, DashboardPresentation.days(points, now: day, calendar: changedCalendar))
        expectEqual(reconfigured.hours(day: day), DashboardPresentation.hours(points, day: day, calendar: changedCalendar))
        let rows = [
            AggregateRow(bucketStartUtc: WireTime.string(day), model: "A", project: "First", tokens: Tokens(input: 100, cachedInput: 90, output: 20, reasoning: 10, responses: 5)),
            AggregateRow(bucketStartUtc: WireTime.string(day), model: "B", project: "First", tokens: Tokens(input: 40, output: 10, responses: 2)),
            AggregateRow(bucketStartUtc: WireTime.string(day.addingTimeInterval(-29 * 86400)), model: "A", project: "Second", tokens: Tokens(input: 30, responses: 1)),
            AggregateRow(bucketStartUtc: WireTime.string(day.addingTimeInterval(-30 * 86400)), model: "A", project: "Outside", tokens: Tokens(input: 999)),
            AggregateRow(bucketStartUtc: WireTime.string(day.addingTimeInterval(86400)), model: "A", project: "Future", tokens: Tokens(input: 999))
        ]
        let remote = DashboardAggregates(rows: rows, now: day, calendar: calendar)
        expectEqual(remote.rollingProjects.map(\.name), ["First", "Second"])
        expectEqual(remote.rollingProjects.map(\.total), [170, 30])
        expectEqual(remote.rollingProjects[0].models, ["A": 120, "B": 50])
        expectEqual(remote.rollingProjects[0].share, 0.85)
        expectEqual(remote.rollingProjects[1].share, 0.15)
        expectEqual(remote.responseCount(day: day), 7)
        expectEqual(remote.tokens(day: day).total, 170)
        let localEquivalent = DashboardAggregates(UsagePoint.fromAggregates(rows), now: day, calendar: calendar)
        expectEqual(remote.rollingProjects, localEquivalent.rollingProjects)
        expectEqual(DashboardAggregates(rows: [], now: day, calendar: calendar).rollingProjects, [])
        let tied = DashboardAggregates([UsagePoint(time: day, model: "A", project: "Z", tokens: Tokens(input: 10)),
                                       UsagePoint(time: day, model: "A", project: "A", tokens: Tokens(input: 10))], now: day, calendar: calendar)
        expectEqual(tied.rollingProjects.map(\.name), ["A", "Z"])
        var persisted = NetworkState()
        persisted.rows = rows; persisted.machineRows = ["Example": rows]
        let decoded = try JSONDecoder().decode(NetworkState.self, from: JSONEncoder().encode(persisted))
        expectEqual(decoded.rows, rows)
        expectEqual(decoded.machineRows?["Example"], rows)
    }
}
