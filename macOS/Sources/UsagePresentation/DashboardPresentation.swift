import Foundation
import UsageCore

public struct DailyValue: Identifiable, Equatable, Sendable {
    public var id: Date { day }
    public let day: Date
    public let total: Int64
    public let models: [String: Int64]
    public let efforts: [String: Int64]
}

public struct HourValue: Identifiable, Equatable, Sendable {
    public var id: String { "\(hour)-\(model)" }
    public let hour: Int
    public let model: String
    public let total: Int64
}

public struct ProjectValue: Identifiable, Equatable, Sendable {
    public var id: String { name }
    public let name: String
    public let total: Int64
}

public enum DashboardPresentation {
    private static func effortLabel(_ point: UsagePoint) -> String {
        let label = point.effort?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return label.isEmpty ? "Unknown" : label
    }
    public static func effortTotals(_ points: [UsagePoint]) -> [String: Int64] {
        Dictionary(grouping: points, by: effortLabel).mapValues { $0.reduce(Int64(0)) { $0 + $1.tokens.total } }
    }
    public static func days(_ points: [UsagePoint], now: Date = Date(), calendar: Calendar = .current) -> [DailyValue] {
        let grouped = Dictionary(grouping: points) { calendar.startOfDay(for: $0.time) }
        return (-29...0).map { offset in
            let day = calendar.date(byAdding: .day, value: offset, to: calendar.startOfDay(for: now))!
            let values = grouped[day] ?? []
            let models = Dictionary(grouping: values, by: \.model).mapValues { $0.reduce(0) { $0 + $1.tokens.total } }
            let efforts = effortTotals(values)
            return DailyValue(day: day, total: values.reduce(0) { $0 + $1.tokens.total }, models: models, efforts: efforts)
        }
    }

    public static func hours(_ points: [UsagePoint], day: Date, calendar: Calendar = .current) -> [HourValue] {
        var values: [String: HourValue] = [:]
        for point in points where calendar.isDate(point.time, inSameDayAs: day) {
            let hour = calendar.component(.hour, from: point.time)
            let id = "\(hour)-\(point.model)"
            values[id] = HourValue(hour: hour, model: point.model, total: (values[id]?.total ?? 0) + point.tokens.total)
        }
        return values.values.sorted { ($0.hour, $0.model) < ($1.hour, $1.model) }
    }

    public static func projects(_ points: [UsagePoint], pinnedDay: Date?, hoveredDay: Date?, hoveredHour: Int?,
                                now: Date = Date(), calendar: Calendar = .current) -> [ProjectValue] {
        let day = calendar.startOfDay(for: hoveredDay ?? pinnedDay ?? now)
        let filtered = points.filter { point in
            calendar.isDate(point.time, inSameDayAs: day) &&
                (hoveredHour == nil || calendar.component(.hour, from: point.time) == hoveredHour)
        }
        let grouped = Dictionary(grouping: filtered, by: \.project)
        let totals: [ProjectValue] = grouped.map { name, values in
            ProjectValue(name: name, total: values.reduce(Int64(0)) { sum, point in sum + point.tokens.total })
        }
        var ranked = totals.sorted { left, right in
            left.total == right.total ? left.name < right.name : left.total > right.total
        }
        if ranked.count > 6 {
            let other = ranked.dropFirst(5).reduce(0) { $0 + $1.total }
            ranked = Array(ranked.prefix(5)) + [ProjectValue(name: "Other", total: other)]
        }
        return ranked
    }

    public static func toggledPin(current: Date?, clicked: Date, today: Date = Date(), calendar: Calendar = .current) -> Date? {
        let value = calendar.startOfDay(for: clicked)
        if calendar.isDate(value, inSameDayAs: today) || current.map({ calendar.isDate($0, inSameDayAs: value) }) == true { return nil }
        return value
    }
}

/// Immutable derived data. Build once when history changes; interaction only looks up buckets.
public struct DashboardAggregates: Sendable {
    public let days: [DailyValue]
    private let calendar: Calendar
    private let countsByDay: [Date: Int]
    private let tokensByDay: [Date: Tokens]
    private let hoursByDay: [Date: [HourValue]]
    private let projectsByDay: [Date: [ProjectValue]]
    private let effortsByHour: [Date: [Int: [String: Int64]]]
    private let projectsByHour: [Date: [Int: [ProjectValue]]]

    public init(_ points: [UsagePoint], now: Date = Date(), calendar: Calendar = .current) {
        self.calendar = calendar
        days = DashboardPresentation.days(points, now: now, calendar: calendar)
        let grouped = Dictionary(grouping: points) { calendar.startOfDay(for: $0.time) }
        countsByDay = grouped.mapValues { $0.count }
        tokensByDay = grouped.mapValues { $0.reduce(Tokens()) { $0 + $1.tokens } }
        hoursByDay = grouped.mapValues { values in
            DashboardPresentation.hours(values, day: values[0].time, calendar: calendar)
        }
        projectsByDay = grouped.mapValues { values in
            DashboardPresentation.projects(values, pinnedDay: values[0].time, hoveredDay: nil, hoveredHour: nil, calendar: calendar)
        }
        effortsByHour = grouped.mapValues { values in
            Dictionary(grouping: values) { calendar.component(.hour, from: $0.time) }
                .mapValues { DashboardPresentation.effortTotals($0) }
        }
        projectsByHour = grouped.mapValues { values in
            Dictionary(grouping: values) { calendar.component(.hour, from: $0.time) }.mapValues { bucket in
                DashboardPresentation.projects(bucket, pinnedDay: bucket[0].time, hoveredDay: nil, hoveredHour: nil, calendar: calendar)
            }
        }
    }
    public func efforts(day: Date, hour: Int) -> [String: Int64] {
        effortsByHour[calendar.startOfDay(for: day)]?[hour] ?? [:]
    }
    public func responseCount(day: Date) -> Int { countsByDay[calendar.startOfDay(for: day)] ?? 0 }
    public func tokens(day: Date) -> Tokens { tokensByDay[calendar.startOfDay(for: day)] ?? Tokens() }
    public func hours(day: Date) -> [HourValue] { hoursByDay[calendar.startOfDay(for: day)] ?? [] }
    public func projects(day: Date, hour: Int?) -> [ProjectValue] {
        let key = calendar.startOfDay(for: day)
        if let hour { return projectsByHour[key]?[hour] ?? [] }
        return projectsByDay[key] ?? []
    }
}
