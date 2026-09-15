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
    public static func days(_ points: [UsagePoint], now: Date = Date(), calendar: Calendar = .current) -> [DailyValue] {
        let grouped = Dictionary(grouping: points) { calendar.startOfDay(for: $0.time) }
        return (-29...0).map { offset in
            let day = calendar.date(byAdding: .day, value: offset, to: calendar.startOfDay(for: now))!
            let values = grouped[day] ?? []
            let models = Dictionary(grouping: values, by: \.model).mapValues { $0.reduce(0) { $0 + $1.tokens.total } }
            let efforts = Dictionary(grouping: values) { point in
                let label = point.effort?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                return label.isEmpty ? "Unknown" : label
            }.mapValues { $0.reduce(Int64(0)) { $0 + $1.tokens.total } }
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
