import Foundation
import CryptoKit
import Darwin

public struct UsagePoint: Codable, Sendable {
    public var time: Date
    public var model: String
    public var project: String
    public var tokens: Tokens
    public var effort: String?
    public init(time: Date, model: String, project: String, tokens: Tokens, effort: String? = nil) {
        self.time = time; self.model = model; self.project = project; self.tokens = tokens; self.effort = effort
    }
    public static func fromAggregates(_ rows: [AggregateRow]) -> [UsagePoint] {
        rows.compactMap { row in
            guard let time = WireTime.date(row.bucketStartUtc) else { return nil }
            return UsagePoint(time: time, model: row.model, project: row.project, tokens: row.tokens, effort: row.effort)
        }
    }
}
public struct ScanResult: Sendable {
    public var points: [UsagePoint] = []
    public var files = 0
    public var unreadableFiles = 0
    public var malformedRecords = 0
    public var filesParsed = 0
    public var bytesRead: Int64 = 0
}
/// Keeps only derived usage and file metadata in memory. Changed files are reparsed
/// in full, so rewrites, truncation and token-format precedence remain correct.
public actor CachedLogScanner {
    private var entries: [String: LogScanner.FileCache] = [:]
    public init() {}
    public func scan(folder: URL, start: Date, end: Date, force: Bool = false) throws -> ScanResult {
        if force { entries.removeAll() }
        return try LogScanner.scan(folder: folder, start: start, end: end, cache: &entries)
    }
}
public enum LogScanner {
    fileprivate struct FileStamp: Equatable {
        let size: Int64
        let modified: Date
        let created: Date
        let inode: UInt64
        init(_ file: URL) throws {
            let attributes = try FileManager.default.attributesOfItem(atPath: file.path)
            size = (attributes[.size] as? NSNumber)?.int64Value ?? -1
            modified = attributes[.modificationDate] as? Date ?? .distantPast
            created = attributes[.creationDate] as? Date ?? .distantPast
            inode = (attributes[.systemFileNumber] as? NSNumber)?.uint64Value ?? 0
        }
    }
    fileprivate struct FileCache {
        let stamp: FileStamp
        let start: Date
        let end: Date
        let result: ScanResult
    }
    public static var defaultFolder: URL { FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex/sessions") }
    public static func scan(folder: URL, start: Date, end: Date) throws -> ScanResult {
        var cache: [String: FileCache] = [:]
        return try scan(folder: folder, start: start, end: end, cache: &cache)
    }
    fileprivate static func scan(folder: URL, start: Date, end: Date, cache: inout [String: FileCache]) throws -> ScanResult {
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
        var seen = Set<String>()
        for case let file as URL in files where file.pathExtension == "jsonl" {
            try Task.checkCancellation()
            result.files += 1
            seen.insert(file.path)
            do {
                guard FileManager.default.isReadableFile(atPath: file.path) else { throw UsageError.invalid("Unreadable session file.") }
                let stamp = try FileStamp(file)
                if let entry = cache[file.path], entry.stamp == stamp, entry.start == start, entry.end == end {
                    result.points += entry.result.points
                    result.malformedRecords += entry.result.malformedRecords
                    continue
                }
                let parsed = try parseFile(file, start: start, end: end)
                result.points += parsed.points; result.malformedRecords += parsed.malformedRecords
                result.filesParsed += 1; result.bytesRead += parsed.bytesRead
                if try FileStamp(file) == stamp {
                    cache[file.path] = FileCache(stamp: stamp, start: start, end: end, result: parsed)
                } else { cache.removeValue(forKey: file.path) }
            } catch is CancellationError { throw CancellationError() }
            catch { cache.removeValue(forKey: file.path); result.unreadableFiles += 1 }
        }
        cache = cache.filter { seen.contains($0.key) }
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
        var bytesRead: Int64 = 0
        while let chunk = try handle.read(upToCount: 65_536), !chunk.isEmpty {
            bytesRead += Int64(chunk.count)
            try Task.checkCancellation()
            buffer.append(chunk)
            autoreleasepool {
                while let newline = newlineIndex(in: buffer) {
                    if !skippingOversizedLine { parser.consume(Data(buffer[..<newline])) }
                    buffer.removeSubrange(...newline); skippingOversizedLine = false
                }
            }
            if buffer.count > 8 * 1024 * 1024 { buffer.removeAll(keepingCapacity: false); skippingOversizedLine = true }
        }
        // An unterminated last record may still be in flight; pick it up next refresh.
        var result = parser.result
        result.bytesRead = bytesRead
        return result
    }
    /// Data's generic firstIndex performs storage lookups for every byte. Use the
    /// platform's bounded byte search without decoding unrelated log content.
    private static func newlineIndex(in data: Data) -> Data.Index? {
        let start = data.startIndex
        return data.withUnsafeBytes { bytes in
            guard let base = bytes.baseAddress, let newline = memchr(base, 10, bytes.count) else { return nil }
            return start + base.distance(to: UnsafeRawPointer(newline))
        }
    }
    public static func parse(_ text: String, start: Date, end: Date) -> ScanResult {
        var parser = Parser(start: start, end: end)
        for line in text.split(separator: "\n") { parser.consume(Data(line.utf8)) }
        return parser.result
    }
    private struct Parser {
        private static let markers = ["\"session_meta\"", "\"turn_context\"", "\"token_count\"", "\"token_usage_record\""].map { Data($0.utf8) }
        private let fractionalDate: ISO8601DateFormatter = {
            let f = ISO8601DateFormatter(); f.formatOptions.insert(.withFractionalSeconds); return f
        }()
        private let wholeDate = ISO8601DateFormatter()
        let start: Date
        let end: Date
        var model = "Unknown model"
        var fallback = "Unknown model"
        var project = "Projectless"
        var models: [String: String] = [:]
        var effort = "Unknown"
        var efforts: [String: String] = [:]
        var counts: [UsagePoint] = []
        var records: [UsagePoint] = []
        var malformed = 0
        var result: ScanResult { ScanResult(points: counts.isEmpty ? records : counts, malformedRecords: malformed) }
        mutating func consume(_ data: Data) {
            guard Self.markers.contains(where: { data.range(of: $0) != nil }) else { return }
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
                let turn = (p["turn_id"] as? String ?? "").lowercased()
                if let value = p["model"] as? String { model = friendly(value); if !turn.isEmpty { models[turn] = model } }
                effort = friendlyEffort(readEffort(p))
                if !turn.isEmpty { efforts[turn] = effort }
                return
            }
            guard let ts = root["timestamp"] as? String,
                  let time = fractionalDate.date(from: ts) ?? wholeDate.date(from: ts) else { malformed += 1; return }
            guard time >= start && time < end else { return }
            if type == "event_msg", p["type"] as? String == "token_count",
               let info = p["info"] as? [String: Any], let usage = info["last_token_usage"] as? [String: Any] {
                counts.append(point(time, model, effort, usage))
            } else if type == "token_usage_record", let usage = p["usage"] as? [String: Any] {
                let turn = (p["turn_id"] as? String ?? "").lowercased()
                records.append(point(time, models[turn] ?? fallback, efforts[turn] ?? "Unknown", usage))
            }
        }
        func point(_ time: Date, _ model: String, _ effort: String, _ usage: [String: Any]) -> UsagePoint {
            func n(_ key: String) -> Int64 { max(0, (usage[key] as? NSNumber)?.int64Value ?? 0) }
            return UsagePoint(time: time, model: model, project: project,
                              tokens: Tokens(input: n("input_tokens"), cachedInput: n("cached_input_tokens"),
                                             output: n("output_tokens"), reasoning: n("reasoning_output_tokens"), responses: 1),
                              effort: effort)
        }
        func friendly(_ value: String) -> String {
            value.replacingOccurrences(of: "gpt-", with: "GPT ", options: .caseInsensitive)
                .replacingOccurrences(of: "codex", with: "Codex", options: .caseInsensitive)
        }
        func readEffort(_ payload: [String: Any]) -> String? {
            if let value = payload["reasoning_effort"] as? String { return value }
            if let value = payload["effort"] as? String { return value }
            if let mode = payload["collaboration_mode"] as? [String: Any],
               let settings = mode["settings"] as? [String: Any] { return settings["reasoning_effort"] as? String }
            return nil
        }
        func friendlyEffort(_ value: String?) -> String {
            guard let raw = value?.trimmingCharacters(in: .whitespacesAndNewlines), !raw.isEmpty else { return "Unknown" }
            switch raw.lowercased() {
            case "low", "light", "minimal": return "Light"
            case "medium": return "Medium"
            case "high": return "High"
            default: return raw.replacingOccurrences(of: "_", with: " ").capitalized
            }
        }
    }
    public static func aggregate(_ points: [UsagePoint], start: Date, end: Date, privacy: ProjectPrivacy = .names, key: Data = Data()) -> [AggregateRow] {
        struct Bucket: Hashable { let time: String; let model: String; let project: String; let projectId: String?; let effort: String }
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
            let bucket = Bucket(time: WireTime.string(hour), model: p.model, project: name, projectId: projectId, effort: p.effort ?? "Unknown")
            buckets[bucket] = (buckets[bucket] ?? Tokens()) + p.tokens
        }
        return buckets.map { AggregateRow(bucketStartUtc: $0.key.time, model: $0.key.model, project: $0.key.project,
                                          tokens: $0.value, projectId: $0.key.projectId, effort: $0.key.effort) }
            .sorted { ($0.bucketStartUtc, $0.project, $0.model, $0.effort ?? "") < ($1.bucketStartUtc, $1.project, $1.model, $1.effort ?? "") }
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
