namespace NexClip.Desktop.Models;

/// <summary>配对响应(POST /api/pair)。</summary>
public sealed class PairResult
{
    public string DeviceId { get; set; } = "";
    public string DeviceToken { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string UserId { get; set; } = "";
}
