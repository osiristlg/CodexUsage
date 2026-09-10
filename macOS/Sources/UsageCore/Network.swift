import Foundation

public protocol ExchangeTransport: Sendable {
    func send(_ request: URLRequest) async throws -> Data
}
public struct HTTPTransport: ExchangeTransport {
    public init() {}
    public func send(_ request: URLRequest) async throws -> Data {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 20; config.timeoutIntervalForResource = 30
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let data: Data
        let response: URLResponse
        do { (data, response) = try await session.data(for: request) }
        catch let error as URLError where error.code == .notConnectedToInternet {
            throw UsageError.invalid("The receiver is unavailable. Check this app's Local Network permission and the receiver address; this does not mean the Internet is offline.")
        }
        guard let response = response as? HTTPURLResponse, (200..<300).contains(response.statusCode) else {
            throw UsageError.invalid("Receiver rejected the request. Check registration, address and subnet settings.")
        }
        guard data.count <= 8 * 1024 * 1024 else { throw UsageError.invalid("Receiver response is too large.") }
        return data
    }
}
public enum NetworkClient {
    public static func endpoint(_ base: String, path: String) throws -> URL {
        guard let url = URL(string: base.trimmingCharacters(in: .whitespacesAndNewlines)),
              ["http", "https"].contains(url.scheme), url.host != nil, url.user == nil, url.password == nil,
              url.query == nil, url.fragment == nil, url.path.isEmpty || url.path == "/" else {
            throw UsageError.invalid("Enter a receiver origin such as http://192.168.1.10:4747.")
        }
        return url.appendingPathComponent(path)
    }
    public static func pair(settings: Settings, passphrase: String, transport: any ExchangeTransport = HTTPTransport()) async throws -> Data {
        struct SaltReply: Decodable { let version: Int; let salt: String }
        let request = URLRequest(url: try endpoint(settings.receiverURL, path: "api/v1/salt/" + settings.clientId))
        let reply = try JSONDecoder().decode(SaltReply.self, from: await transport.send(request))
        guard reply.version == 1, let salt = Data(base64Encoded: reply.salt), salt.count == 16 else {
            throw UsageError.invalid("Receiver pairing information is incompatible.")
        }
        return try AggregateProtocol.deriveKey(passphrase: passphrase, salt: salt)
    }
    public static func exchange(settings: Settings, payload: SyncPayload, key: Data, now: Date = Date(),
                                transport: any ExchangeTransport = HTTPTransport()) async throws -> ExchangeReply {
        let envelope = try AggregateProtocol.encrypt(payload, clientId: settings.clientId, key: key, now: now)
        var request = URLRequest(url: try endpoint(settings.receiverURL, path: "api/v1/exchange"))
        request.httpMethod = "POST"; request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(envelope)
        // Reuse the exact envelope/request ID on a transient transport failure.
        let data: Data
        do { data = try await transport.send(request) }
        catch let error as URLError where [.networkConnectionLost, .timedOut].contains(error.code) {
            try Task.checkCancellation()
            data = try await transport.send(request)
        }
        let replyEnvelope = try JSONDecoder().decode(Envelope.self, from: data)
        guard replyEnvelope.clientId == settings.clientId,
              let created = WireTime.date(replyEnvelope.createdAtUtc), abs(created.timeIntervalSinceNow) <= 900 else {
            throw UsageError.invalid("Receiver reply identity or timestamp is invalid.")
        }
        let reply = try JSONDecoder().decode(ExchangeReply.self, from: AggregateProtocol.open(replyEnvelope, key: key))
        guard reply.accepted else { throw UsageError.invalid(reply.message) }
        return reply
    }
    public static func payload(settings: Settings, points: [UsagePoint], window: SyncWindow, key: Data, query: Bool = false) -> SyncPayload {
        SyncPayload(kind: query ? "query" : (window.full ? "full" : "incremental"), machineName: settings.machineName,
                    rangeStartUtc: WireTime.string(window.start), rangeEndUtc: WireTime.string(window.end),
                    combinedStartUtc: WireTime.string(window.today), combinedEndUtc: WireTime.string(window.tomorrow),
                    rows: query ? [] : LogScanner.aggregate(points, start: window.start, end: window.end,
                                                           privacy: settings.projectPrivacy, key: key))
    }
}

public enum SyncEngine {
    public static func sync(settings: Settings, points: [UsagePoint], previous: NetworkState, key: Data,
                            force: Bool = false, query: Bool = false, now: Date = Date(),
                            calendar: Calendar = .current, transport: any ExchangeTransport = HTTPTransport()) async throws -> NetworkState {
        let window = SyncWindow.make(now: now, lastFull: previous.lastFull, force: force || previous.needsFullSync == true, calendar: calendar)
        let payload = NetworkClient.payload(settings: settings, points: points, window: window, key: key, query: query)
        let reply = try await NetworkClient.exchange(settings: settings, payload: payload, key: key, now: now, transport: transport)
        var next = previous
        next.lastSuccess = now; next.combinedDay = window.today; next.combined = reply.combined; next.machines = reply.machines
        if window.full && !query { next.lastFull = window.today; next.needsFullSync = false }
        return next
    }
}
