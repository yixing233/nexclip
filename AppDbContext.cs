using Microsoft.EntityFrameworkCore;

namespace NexClipServer;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ClipboardEntry> Entries => Set<ClipboardEntry>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<ActivityLog> Activities => Set<ActivityLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ClipboardEntry>().HasIndex(e => e.CreatedAt);
        b.Entity<ClipboardEntry>().HasIndex(e => e.ContentHash);
        b.Entity<ActivityLog>().HasIndex(e => e.CreatedAt);
    }
}
