namespace SyncClipboard.Desktop.Models;

/// <summary>配对码生成响应(POST /api/pairing-codes)。</summary>
public sealed class PairingCodeResult
{
    public string Code { get; set; } = "";
    public DateTime ExpiresAt { get; set; }   // UTC
    public string? UserId { get; set; }
    public string? DeviceToken { get; set; }
    public string? QrPayload { get; set; }
}
