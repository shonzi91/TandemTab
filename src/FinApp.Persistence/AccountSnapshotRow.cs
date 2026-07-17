namespace FinApp.Persistence;

/// <summary>
/// Server-side storage of a shared account's full aggregate as a single snapshot blob, keyed by account.
/// <see cref="Payload"/> is produced by <c>AccountSnapshotSerializer</c> on the client and is encrypted at rest
/// (see <c>ISnapshotCipher</c> — envelope/KMS in production). <see cref="Version"/> drives optimistic concurrency
/// between contributors.
/// <para>
/// This is <b>not</b> opaque to the server, and end-to-end encryption is <b>not</b> a live option: the server
/// already decrypts and fully deserializes the payload to render exports (<c>AccountExportService</c>), and bank
/// sync stores real transactions server-side under a server-held key. The trust model is "the server may read your
/// data"; confidentiality comes from encryption at rest plus access control, not from keeping the server blind.
/// </para>
/// <para>
/// Consequence worth weighing before treating whole-blob storage as fixed: every mutation rewrites the entire
/// account, so save cost scales with total history rather than with the size of the edit. Nothing in the trust
/// model requires that — it is the last thing holding the shape of a design we no longer follow.
/// </para>
/// </summary>
public sealed class AccountSnapshotRow
{
    public Guid AccountId { get; set; }
    public string Payload { get; set; } = "";
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
