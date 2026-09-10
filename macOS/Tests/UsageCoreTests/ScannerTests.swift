import Foundation
import UsageCore
import UsagePresentation

struct ScannerTests {
    func run() throws {
        let start = WireTime.date("2026-09-10T00:00:00Z")!
        let end = start.addingTimeInterval(86400)
        let metadata = #"{"type":"session_meta","payload":{"cwd":"/private/secret/Example"}}"# + "\n" +
            #"{"type":"turn_context","payload":{"turn_id":"a","model":"gpt-5.6-sol"}}"# + "\n"
        let legacy = #"{"timestamp":"2026-09-10T12:15:00Z","type":"token_usage_record","payload":{"turn_id":"a","usage":{"input_tokens":100,"output_tokens":20}}}"#
        let event = #"{"timestamp":"2026-09-10T12:15:00Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":80,"output_tokens":20,"reasoning_output_tokens":5}}}}"#
        let result = LogScanner.parse(metadata + legacy + "\n" + event + "\n" + #"{"type":"token_usage_record","broken"#,
                                      start: start, end: end)
        expectEqual(result.points.count, 1)
        expectEqual(result.malformedRecords, 1)
        expectEqual(result.points[0].tokens.total, 120)
        expectEqual(result.points[0].model, "GPT 5.6-sol")
        expectEqual(LogScanner.parse(metadata + legacy, start: start, end: end).points.count, 1)
        expectEqual(LogScanner.parse(metadata + event, start: end, end: end.addingTimeInterval(86400)).points.count, 0)
        let key = Data(repeating: 1, count: 32)
        let rows = LogScanner.aggregate(result.points, start: start, end: end, privacy: .anonymous, key: key)
        expectEqual(rows[0].bucketStartUtc, "2026-09-10T12:00:00Z")
        expectEqual(rows[0].project, "Project 32F458A8")
        expectEqual(rows[0].projectId, rows[0].project)
        let encoded = String(decoding: try JSONEncoder().encode(rows), as: UTF8.self)
        expectEqual(encoded.contains("Example"), false); expectEqual(encoded.contains("secret"), false)
        let named = LogScanner.aggregate(result.points, start: start, end: end, privacy: .names, key: key)
        expectEqual(named[0].project, "Example"); expectEqual(named[0].projectId, rows[0].project)
        let ungrouped = LogScanner.aggregate(result.points + result.points, start: start, end: end, privacy: .none)
        expectEqual(ungrouped[0].tokens.total, 240); expectEqual(ungrouped[0].projectId, nil)
        expectEqual(String(decoding: try JSONEncoder().encode(ungrouped), as: UTF8.self).contains("projectId"), false)
        var cal = Calendar(identifier: .gregorian); cal.timeZone = TimeZone(identifier: "America/New_York")!
        let before = WireTime.date("2026-09-10T05:59:00Z")!
        expectEqual(SyncWindow.make(now: before, lastFull: nil, force: false, calendar: cal).full, false)
        let after = WireTime.date("2026-09-10T06:01:00Z")!
        let full = SyncWindow.make(now: after, lastFull: nil, force: false, calendar: cal)
        expectEqual(full.full, true)
        expectEqual(cal.dateComponents([.day], from: full.start, to: full.end).day, 30)
        let incremental = SyncWindow.make(now: after, lastFull: after, force: false, calendar: cal)
        expectEqual(incremental.full, false); expectEqual(incremental.end.timeIntervalSince(incremental.start), 7200)
        expectEqual(SyncWindow.make(now: before, lastFull: before, force: true, calendar: cal).full, true)
        let dst = SyncWindow.make(now: WireTime.date("2026-03-09T07:00:00Z")!, lastFull: nil, force: true, calendar: cal)
        expectEqual(dst.end.timeIntervalSince(dst.start), 30 * 86400 - 3600)
        // Demonstrate the inherited v1 range/bucket issue without changing receiver semantics.
        var india = Calendar(identifier: .gregorian); india.timeZone = TimeZone(identifier: "Asia/Kolkata")!
        let indiaWindow = SyncWindow.make(now: after, lastFull: nil, force: true, calendar: india)
        let firstBucket = Date(timeIntervalSince1970: floor(indiaWindow.start.timeIntervalSince1970 / 3600) * 3600)
        expectEqual(firstBucket < indiaWindow.start, true)
        expectThrows(try NetworkClient.endpoint("http://user:pass@host/", path: "api"))

        let nextDay = start.addingTimeInterval(86_400)
        let fixturePoints = [
            FixturePoint(time: start.addingTimeInterval(9 * 3_600), model: "A", project: "One", tokens: Tokens(input: 10)),
            FixturePoint(time: start.addingTimeInterval(9 * 3_600 + 10), model: "B", project: "Two", tokens: Tokens(input: 20)),
            FixturePoint(time: nextDay.addingTimeInterval(10 * 3_600), model: "A", project: "One", tokens: Tokens(input: 40))
        ]
        let presentationPoints = try JSONDecoder().decode([UsagePoint].self, from: JSONEncoder().encode(fixturePoints))
        var utc = Calendar(identifier: .gregorian); utc.timeZone = TimeZone(secondsFromGMT: 0)!
        let hours = DashboardPresentation.hours(presentationPoints, day: start, calendar: utc)
        expectEqual(hours.reduce(Int64(0)) { $0 + $1.total }, 30)
        expectEqual(DashboardPresentation.projects(presentationPoints, pinnedDay: start, hoveredDay: nil, hoveredHour: 9, calendar: utc).map(\.name), ["Two", "One"])
        expectEqual(DashboardPresentation.projects(presentationPoints, pinnedDay: start, hoveredDay: nextDay, hoveredHour: nil, calendar: utc).first?.total, 40)
        let pin = DashboardPresentation.toggledPin(current: nil, clicked: start, today: nextDay, calendar: utc)
        expectEqual(pin, start)
        expectEqual(DashboardPresentation.toggledPin(current: pin, clicked: start, today: nextDay, calendar: utc), nil)
        expectEqual(DashboardPresentation.toggledPin(current: start, clicked: nextDay, today: nextDay, calendar: utc), nil)
    }
}

private struct FixturePoint: Codable {
    let time: Date
    let model: String
    let project: String
    let tokens: Tokens
}
actor MockReceiver: ExchangeTransport {
    let key = Data(repeating: 1, count: 32)
    var attempts = 0
    var firstBody: Data?
    var reject = false
    func setReject() { reject = true }
    func send(_ request: URLRequest) async throws -> Data {
        attempts += 1
        if attempts == 1 { firstBody = request.httpBody; throw URLError(.networkConnectionLost) }
        if attempts == 2 { expectEqual(request.httpBody, firstBody) }
        let envelope = try JSONDecoder().decode(Envelope.self, from: request.httpBody!)
        let payload = try JSONDecoder().decode(SyncPayload.self, from: AggregateProtocol.open(envelope, key: key))
        expectEqual(payload.kind, "full")
        expectEqual(payload.rows.allSatisfy { $0.project.hasPrefix("Project ") }, true)
        if reject { throw UsageError.invalid("Test failure") }
        let reply = try JSONSerialization.data(withJSONObject: ["accepted": true, "message": "Accepted", "receivedAtUtc": WireTime.string(Date()),
            "combined": ["input": 100, "cachedInput": 80, "output": 20, "reasoning": 5, "responses": 1], "machines": [:]])
        return try JSONEncoder().encode(AggregateProtocol.seal(reply, clientId: envelope.clientId,
            requestId: UUID().uuidString, createdAtUtc: WireTime.string(Date()), key: key))
    }
}
func networkChecks() async throws {
    let receiver = MockReceiver()
    var initial = NetworkState()
    initial.needsFullSync = true
    initial = try JSONDecoder().decode(NetworkState.self, from: JSONEncoder().encode(initial))
    expectEqual(initial.needsFullSync, true)
    let result = try await SyncEngine.sync(settings: Settings(), points: [], previous: initial,
        key: Data(repeating: 1, count: 32), force: true, transport: receiver)
    expectEqual(result.combined?.total, 120)
    expectEqual(result.lastFull != nil, true)
    expectEqual(result.needsFullSync, false)
    await receiver.setReject()
    do {
        _ = try await SyncEngine.sync(settings: Settings(), points: [], previous: initial,
            key: Data(repeating: 1, count: 32), force: true, transport: receiver)
        preconditionFailure("Expected failure")
    } catch { expectEqual(initial.lastFull, nil) }
}
