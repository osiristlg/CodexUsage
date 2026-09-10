import Foundation
import CryptoKit

public struct UsagePoint: Codable, Sendable {
    public var time: Date
    public var model: String
    public var project: String
    public var tokens: Tokens
}
public struct ScanResult: Sendable {
    public var points: [UsagePoint] = []
    public var files = 0
    public var unreadableFiles = 0
    public var malformedRecords = 0
}
public enum LogScanner {
    public static var defaultFolder: URL { FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex/sessions") }
    public static func scan(folder: URL, start: Date, end: Date) throws -> ScanResult {
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: folder.path, isDirectory: &isDirectory), isDirectory.boolValue else {
            throw UsageError.invalid("Session folder does not exist. Choose a readable Codex sessions folder.")
        }
        var enumerationErrors = 0
        guard let files = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: [.isRegularFileKey],
                                                         options: [.skipsHiddenFiles], errorHandler: { _, _ in enumerationErrors += 1; return true }) else {
            throw UsageError.invalid("Session folder could not be read.")
        }
        var result = ScanResult()
        for case let file as URL in files where file.pathExtension == "jsonl" {
            try Task.checkCancellation()
            result.files += 1
            do {
                let parsed = try parseFile(file, start: start, end: end)
                result.points += parsed.points; result.malformedRecords += parsed.malformedRecords
            } catch is CancellationError { throw CancellationError() }
            catch { result.unreadableFiles += 1 }
        }
        result.unreadableFiles += enumerationErrors
        return result
    }
    // Stream by line, retaining only derived points, never a complete log in memory or cache.
    private static func parseFile(_ file: URL, start: Date, end: Date) throws -> ScanResult {
        let handle = try FileHandle(forReadingFrom: file)
        defer { try? handle.close() }
        var parser = Parser(start: start, end: end)
        var buffer = Data()
        var skippingOversizedLine = false
        while let chunk = try handle.read(upToCount: 65_536), !chunk.isEmpty {
            try Task.checkCancellation()
            buffer.append(chunk)
            while let newline = buffer.firstIndex(of: 10) {
                if !skippingOversizedLine { parser.consume(Data(buffer[..<newline])) }
                buffer.removeSubrange(...newline); skippingOversizedLine = false
            }
            if buffer.count > 8 * 1024 * 1024 { buffer.removeAll(keepingCapacity: false); skippingOversizedLine = true }
        }
        // An unterminated last record may still be in flight; pick it up next refresh.
        return parser.result
    }
    public static func parse(_ text: String, start: Date, end: Date) -> ScanResult {
        var parser = Parser(start: start, end: end)
        for line in text.split(separator: "\n") { parser.consume(Data(line.utf8)) }
        return parser.result
    }
    private struct Parser {
        let start: Date
        let end: Date
        var model = "Unknown model"
        var fallback = "Unknown model"
        var project = "Projectless"
        var models: [String: String] = [:]
        var counts: [UsagePoint] = []
        var records: [UsagePoint] = []
        var malformed = 0
        var result: ScanResult { ScanResult(points: counts.isEmpty ? records : counts, malformedRecords: malformed) }
        mutating func consume(_ data: Data) {
            guard let line = String(data: data, encoding: .utf8),
                  ["\"session_meta\"", "\"turn_context\"", "\"token_count\"", "\"token_usage_record\""].contains(where: line.contains) else { return }
            guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
                  let type = root["type"] as? String, let p = root["payload"] as? [String: Any] else { malformed += 1; return }
            if type == "session_meta" {
                if let cwd = p["cwd"] as? String, !cwd.isEmpty {
                    project = cwd.replacingOccurrences(of: "\\", with: "/").split(separator: "/").last.map(String.init) ?? "Projectless"
                }
                if let bi = p["base_instructions"] as? [String: Any], let provenance = bi["provenance"] as? [String: Any],
                   let value = provenance["model"] as? String { model = friendly(value); fallback = model }
                return
            }
            if type == "turn_context" {
                if let turn = p["turn_id"] as? String, let value = p["model"] as? String {
                    model = friendly(value); models[turn.lowercased()] = model
                }
                return
            }
            guard let ts = root["timestamp"] as? String, let time = WireTime.date(ts) else { malformed += 1; return }
            guard time >= start && time < end else { return }
            if type == "event_msg", p["type"] as? String == "token_count",
               let info = p["info"] as? [String: Any], let usage = info["last_token_usage"] as? [String: Any] {
                counts.append(point(time, model, usage))
            } else if type == "token_usage_record", let usage = p["usage"] as? [String: Any] {
                let turn = (p["turn_id"] as? String ?? "").lowercased()
                records.append(point(time, models[turn] ?? fallback, usage))
            }
        }
        func point(_ time: Date, _ model: String, _ usage: [String: Any]) -> UsagePoint {
            func n(_ key: String) -> Int64 { max(0, (usage[key] as? NSNumber)?.int64Value ?? 0) }
            return UsagePoint(time: time, model: model, project: project,
                              tokens: Tokens(input: n("input_tokens"), cachedInput: n("cached_input_tokens"),
                                             output: n("output_tokens"), reasoning: n("reasoning_output_tokens"), responses: 1))
        }
        func friendly(_ value: String) -> String {
            value.replacingOccurrences(of: "gpt-", with: "GPT ", options: .caseInsensitive)
                .replacingOccurrences(of: "codex", with: "Codex", options: .caseInsensitive)
        }
    }
    public static func aggregate(_ points: [UsagePoint], start: Date, end: Date, privacy: ProjectPrivacy = .names, key: Data = Data()) -> [AggregateRow] {
        struct Bucket: Hashable { let time: String; let model: String; let project: String; let projectId: String? }
        func anonymousProject(_ project: String) -> String {
            let digest = HMAC<SHA256>.authenticationCode(for: Data(project.utf8), using: SymmetricKey(data: key))
            return "Project " + digest.prefix(4).map { String(format: "%02X", $0) }.joined()
        }
        var buckets: [Bucket: Tokens] = [:]
        for p in points where p.time >= start && p.time < end {
            let hour = Date(timeIntervalSince1970: floor(p.time.timeIntervalSince1970 / 3600) * 3600)
            let name: String
            let projectId: String?
            switch privacy {
            case .names:
                name = p.project; projectId = anonymousProject(p.project)
            case .none:
                name = "All projects"; projectId = nil
            case .anonymous:
                name = anonymousProject(p.project); projectId = name
            }
            let bucket = Bucket(time: WireTime.string(hour), model: p.model, project: name, projectId: projectId)
            buckets[bucket] = (buckets[bucket] ?? Tokens()) + p.tokens
        }
        return buckets.map { AggregateRow(bucketStartUtc: $0.key.time, model: $0.key.model, project: $0.key.project,
                                          tokens: $0.value, projectId: $0.key.projectId) }
            .sorted { ($0.bucketStartUtc, $0.project, $0.model) < ($1.bucketStartUtc, $1.project, $1.model) }
    }
}
public enum ProjectPrivacy: String, Codable, Sendable, CaseIterable { case anonymous, none, names }
public struct SyncWindow: Sendable {
    public var full: Bool
    public var start: Date
    public var end: Date
    public var today: Date
    public var tomorrow: Date
    public static func make(now: Date, lastFull: Date?, force: Bool, calendar: Calendar = .current) -> Self {
        let today = calendar.startOfDay(for: now)
        let tomorrow = calendar.date(byAdding: .day, value: 1, to: today)!
        let full = force || (calendar.component(.hour, from: now) >= 2 && (lastFull == nil || !calendar.isDate(lastFull!, inSameDayAs: now)))
        let hour = Date(timeIntervalSince1970: floor(now.timeIntervalSince1970 / 3600) * 3600)
        return Self(full: full, start: full ? calendar.date(byAdding: .day, value: -29, to: today)! : hour.addingTimeInterval(-3600),
                    end: full ? tomorrow : hour.addingTimeInterval(3600), today: today, tomorrow: tomorrow)
    }
}
