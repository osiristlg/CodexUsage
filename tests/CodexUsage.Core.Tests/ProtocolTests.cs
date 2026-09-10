using System.Security.Cryptography;
using CodexUsage.Core;
using Xunit;

namespace CodexUsage.Core.Tests;

public sealed class ProtocolTests
{
    private static readonly DateTime Created = new(2026, 9, 10, 12, 34, 56, DateTimeKind.Utc);
    private const string ClientId = "desktop-a7f3";
    private const string RequestId = "12345678-1234-1234-1234-123456789abc";

    [Fact]
    public void RoundTripPreservesAggregatePayload()
    {
        var key = AggregateProtocol.DeriveKey("correct horse battery staple", Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        var payload = SamplePayload();

        var envelope = AggregateProtocol.Encrypt(ClientId, RequestId, Created, payload, key);
        var decrypted = AggregateProtocol.Decrypt<SyncPayload>(envelope, key);

        Assert.Equal(payload.Kind, decrypted.Kind);
        Assert.Equal(payload.MachineName, decrypted.MachineName);
        Assert.Equal(payload.RangeStartUtc, decrypted.RangeStartUtc);
        Assert.Equal(payload.RangeEndUtc, decrypted.RangeEndUtc);
        Assert.Equal(payload.CombinedStartUtc, decrypted.CombinedStartUtc);
        Assert.Equal(payload.CombinedEndUtc, decrypted.CombinedEndUtc);
        Assert.Equal(payload.Rows, decrypted.Rows);
    }

    [Fact]
    public void WrongKeyAndTamperingAreRejected()
    {
        var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var key = AggregateProtocol.DeriveKey("right password", salt);
        var wrong = AggregateProtocol.DeriveKey("wrong password", salt);
        var envelope = AggregateProtocol.Encrypt(ClientId, RequestId, Created, SamplePayload(), key);

        Assert.Throws<AuthenticationTagMismatchException>(() => AggregateProtocol.Decrypt<SyncPayload>(envelope, wrong));
        var bytes = Convert.FromBase64String(envelope.Ciphertext);
        bytes[0] ^= 1;
        var tampered = envelope with { Ciphertext = Convert.ToBase64String(bytes) };
        Assert.Throws<AuthenticationTagMismatchException>(() => AggregateProtocol.Decrypt<SyncPayload>(tampered, key));
    }

    [Fact]
    public void DeterministicVectorRemainsStable()
    {
        var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var nonce = Enumerable.Range(16, 12).Select(i => (byte)i).ToArray();
        var key = AggregateProtocol.DeriveKey("portable-test-vector", salt);
        var envelope = AggregateProtocol.EncryptCore(ClientId, RequestId, Created, SamplePayload(), key, nonce);

        Assert.Equal("EBESExQVFhcYGRob", envelope.Nonce);
        Assert.Equal(VectorCiphertext, envelope.Ciphertext);
        Assert.Equal(VectorTag, envelope.Tag);
    }

    private static SyncPayload SamplePayload() => new(
        "incremental",
        "Workstation",
        new DateTime(2026, 9, 10, 11, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 9, 10, 13, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 9, 10, 4, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 9, 11, 4, 0, 0, DateTimeKind.Utc),
        [new AggregateRow(new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc), "GPT 5.6-sol", "Project 1", new TokenCounts(100, 80, 20, 5, 1))]);

    // Filled from the implementation once, then held stable as a cross-platform compatibility contract.
    private const string VectorCiphertext = "E5b4y8RZ4qxL1NMnnqcOL+3oupLpKme4knhUrPc/rusKe1+Mifo99zvn6BHMe27ZI1P73CeNrOgvDb1P7T8ka7PL4chu6eNvl0qKqek2txyEv1x1vXdivqon+5eTFOAPFScGf6S9Qs6VvtGIvKzp0yOdWfT+QpRkoXBWVTlaHU7ZmfZT4X/YNzOy9pONbrzM+oDOpx9diaVHMFRuGZQB/NlO9jLy2Z/nLCWPxRMdbcPfqpSG0ev/OOVmZU+m+AbMe/LEjDP/kBs0DUJJpbsgtIvnuqHNDRCNdj9BZahOUCTcBY1vZP/OEIlv1y4bFhOWppXgHXAD6slcIS29TcCgxwOMgqCj13vk8FYogSUMlphOoVVvgWM7z8fkZUSlVXaaaZ+fv1ZzKZbhE9iUy/Hb+y0FRwuN6P4bAvG4jFda6FaM9DshXdZzuHpVtKhjzxAoIvBp7XWZmjTTM2sf07NPzdhXDSG0n35tHcFUme+mMyCSTXiXkBN7+yLrJrHSU3swAt0RO4JfsK0r6YQ=";
    private const string VectorTag = "NVjThp5uoiRH5xlmQzfR6A==";
}
