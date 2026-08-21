namespace SyncClipboard.Desktop.Models;

/// <summary>轮询配对状态响应(GET /api/pair/status)。</summary>
public sealed class PairStatusResult
{
    public string Status { get; set; } = ""; // "pending" | "approved" | "rejected" | "expired" | "not-found"
    public string? UserId { get; set; }
    public string? DeviceToken { get; set; }
}
