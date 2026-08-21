namespace SyncClipboard.Desktop.Models;

/// <summary>待确认配对请求项(GET /api/pairing-requests)。</summary>
public sealed class PairingRequestInfo
{
    public string Code { get; set; } = "";
    public string GeneratorId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string Status { get; set; } = ""; // "open" | "pending" | "approved" | "rejected"
    public string? CreatedAt { get; set; }
}
