using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexUsage.Core;

public sealed record TokenCounts(long Input, long CachedInput, long Output, long Reasoning, long Responses)
{
    public long Total => Input + Output;
}

public sealed record AggregateRow(
    DateTime BucketStartUtc,
    string Model,
    string Project,
    TokenCounts Tokens)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; init; }
}

public sealed record SyncPayload(
    string Kind,
    string MachineName,
    DateTime RangeStartUtc,
    DateTime RangeEndUtc,
    DateTime CombinedStartUtc,
    DateTime CombinedEndUtc,
    IReadOnlyList<AggregateRow> Rows)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? QueryStartUtc { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? QueryEndUtc { get; init; }
}

public sealed record ExchangeReply(
    bool Accepted,
    string Message,
    DateTime ReceivedAtUtc,
    TokenCounts Combined,
    IReadOnlyDictionary<string, TokenCounts> Machines)
{
    public IReadOnlyList<AggregateRow> Rows { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<AggregateRow>> MachineRows { get; init; } =
        new Dictionary<string, IReadOnlyList<AggregateRow>>();
}

public sealed record EncryptedEnvelope(
    int Version,
    string ClientId,
    string RequestId,
    DateTime CreatedAtUtc,
    string Nonce,
    string Ciphertext,
    string Tag);

public static class AggregateProtocol
{
    public const int Version = 1;
    public const int SaltBytes = 16;
    public const int KeyBytes = 32;
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int Pbkdf2Iterations = 600_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltBytes);

    public static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt)
    {
        if (string.IsNullOrWhiteSpace(passphrase)) throw new ArgumentException("A passphrase is required.", nameof(passphrase));
        if (salt.Length != SaltBytes) throw new ArgumentException($"Salt must contain {SaltBytes} bytes.", nameof(salt));
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormKC)),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
    }

    public static EncryptedEnvelope Encrypt<T>(string clientId, string requestId, DateTime createdAtUtc, T payload, ReadOnlySpan<byte> key)
        => EncryptCore(clientId, requestId, createdAtUtc, payload, key, RandomNumberGenerator.GetBytes(NonceBytes));

    internal static EncryptedEnvelope EncryptCore<T>(string clientId, string requestId, DateTime createdAtUtc, T payload,
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> suppliedNonce)
    {
        ValidateHeader(clientId, requestId, createdAtUtc, key);
        if (suppliedNonce.Length != NonceBytes) throw new CryptographicException("Invalid nonce length.");
        var nonce = suppliedNonce.ToArray();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(clientId, requestId, createdAtUtc));
            return new EncryptedEnvelope(
                Version,
                clientId,
                requestId,
                createdAtUtc.ToUniversalTime(),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext),
                Convert.ToBase64String(tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static T Decrypt<T>(EncryptedEnvelope envelope, ReadOnlySpan<byte> key)
    {
        if (envelope.Version != Version) throw new CryptographicException("Unsupported protocol version.");
        ValidateHeader(envelope.ClientId, envelope.RequestId, envelope.CreatedAtUtc, key);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);
        if (nonce.Length != NonceBytes || tag.Length != TagBytes) throw new CryptographicException("Invalid encrypted envelope.");
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext,
                AssociatedData(envelope.ClientId, envelope.RequestId, envelope.CreatedAtUtc));
            return JsonSerializer.Deserialize<T>(plaintext, JsonOptions)
                ?? throw new JsonException("The decrypted payload was empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] AssociatedData(string clientId, string requestId, DateTime createdAtUtc) =>
        Encoding.UTF8.GetBytes($"{Version}\n{clientId}\n{requestId}\n{createdAtUtc.ToUniversalTime():O}");

    private static void ValidateHeader(string clientId, string requestId, DateTime createdAtUtc, ReadOnlySpan<byte> key)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 80) throw new CryptographicException("Invalid client identifier.");
        if (!Guid.TryParse(requestId, out _)) throw new CryptographicException("Invalid request identifier.");
        if (createdAtUtc.Kind == DateTimeKind.Unspecified) throw new CryptographicException("Timestamp must include a time zone.");
        if (key.Length != KeyBytes) throw new CryptographicException("Invalid encryption key.");
    }
}
