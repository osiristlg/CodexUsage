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
    }
}
