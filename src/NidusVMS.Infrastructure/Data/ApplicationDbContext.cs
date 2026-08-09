using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NidusVMS.Core.Entities;
using NidusVMS.Infrastructure.Security;

namespace NidusVMS.Infrastructure.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    // datetime2 has no Kind, so a value read back from SQL Server is DateTimeKind.Unspecified — left
    // alone, ToString("O") on it drops the trailing "Z", browsers then parse it as local time, and
    // every relative/absolute timestamp in the app is silently wrong by the local UTC offset. This
    // stamps every DateTime column as UTC on the way out of the database, once, for the whole model.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    // ── Config / identity ────────────────────────────────────────────────────
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<SettingOverride> SettingOverrides => Set<SettingOverride>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<CameraAccess> CameraAccesses => Set<CameraAccess>();
    public DbSet<AppVersion> AppVersions => Set<AppVersion>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // ── Cameras ──────────────────────────────────────────────────────────────
    public DbSet<Camera> Cameras => Set<Camera>();
    public DbSet<CameraGroup> CameraGroups => Set<CameraGroup>();
    public DbSet<CameraCapabilities> CameraCapabilities => Set<CameraCapabilities>();
    public DbSet<CameraStream> CameraStreams => Set<CameraStream>();

    // ── Nodes / recording ────────────────────────────────────────────────────
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<Segment> Segments => Set<Segment>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ── Setting ──────────────────────────────────────────────────────────
        builder.Entity<Setting>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(250).IsRequired();
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Description).HasMaxLength(500);
        });

        // ── SettingOverride ──────────────────────────────────────────────────
        builder.Entity<SettingOverride>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(250).IsRequired();
            e.HasIndex(x => new { x.Scope, x.ScopeId, x.Key }).IsUnique();
        });

        // ── Permission ───────────────────────────────────────────────────────
        builder.Entity<Permission>(e =>
        {
            e.Property(x => x.Resource).HasMaxLength(100).IsRequired();
            e.Property(x => x.Action).HasMaxLength(50).IsRequired();
            e.HasIndex(x => new { x.RoleId, x.Resource, x.Action }).IsUnique();
        });

        // ── CameraAccess ─────────────────────────────────────────────────────
        builder.Entity<CameraAccess>(e =>
        {
            e.Property(x => x.PrincipalId).HasMaxLength(450).IsRequired();
            e.HasIndex(x => new { x.PrincipalType, x.PrincipalId, x.ScopeType, x.ScopeId });
        });

        // ── AppVersion ───────────────────────────────────────────────────────
        builder.Entity<AppVersion>(e =>
        {
            e.Property(x => x.Notes).HasMaxLength(1000);
        });

        // ── AuditLog ─────────────────────────────────────────────────────────
        builder.Entity<AuditLog>(e =>
        {
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.IpAddress).HasMaxLength(45);
            e.HasIndex(x => x.OccurredAt);
        });

        // ── CameraGroup ──────────────────────────────────────────────────────
        builder.Entity<CameraGroup>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.MaterializedPath).HasMaxLength(1000).IsRequired();
            e.HasOne(x => x.Parent).WithMany(x => x.Children)
                .HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        // ── Camera ───────────────────────────────────────────────────────────
        var encryptedNullable = new EncryptedNullableStringConverter();
        builder.Entity<Camera>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Host).HasMaxLength(255).IsRequired();
            e.Property(x => x.DeviceServiceUri).HasMaxLength(500).IsRequired();
            e.Property(x => x.Username).HasConversion(encryptedNullable).HasMaxLength(500);
            e.Property(x => x.Password).HasConversion(encryptedNullable).HasMaxLength(500);
            e.Property(x => x.Manufacturer).HasMaxLength(200);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.FirmwareVersion).HasMaxLength(100);
            e.Property(x => x.SerialNumber).HasMaxLength(100);
            e.HasOne(x => x.Group).WithMany(g => g.Cameras)
                .HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Node).WithMany(n => n.Cameras)
                .HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.Name);
        });

        // ── CameraCapabilities (1:1 with Camera) ────────────────────────────
        builder.Entity<CameraCapabilities>(e =>
        {
            e.HasKey(x => x.CameraId);
            e.HasOne(x => x.Camera).WithOne(c => c.Capabilities)
                .HasForeignKey<CameraCapabilities>(x => x.CameraId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── CameraStream ─────────────────────────────────────────────────────
        builder.Entity<CameraStream>(e =>
        {
            e.Property(x => x.RtspUri).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ProfileToken).HasMaxLength(200).IsRequired();
            e.Property(x => x.Codec).HasMaxLength(50);
            e.Property(x => x.AudioCodec).HasMaxLength(50);
            e.Property(x => x.CustomName).HasMaxLength(200);
            e.HasOne(x => x.Camera).WithMany(c => c.Streams)
                .HasForeignKey(x => x.CameraId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CameraId, x.Role });
        });

        // ── Node ─────────────────────────────────────────────────────────────
        builder.Entity<Node>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.StorageRootPath).HasMaxLength(500);
            e.Property(x => x.ApiKeyHash).HasMaxLength(200).IsRequired();
            e.Property(x => x.PreviousApiKeyHash).HasMaxLength(200);
            e.Property(x => x.Version).HasMaxLength(50);
            e.Property(x => x.Platform).HasMaxLength(50);
            e.Property(x => x.LastIpAddress).HasMaxLength(45);
            e.Property(x => x.MediaSigningKey).HasConversion(new EncryptedNullableStringConverter()).HasMaxLength(500);
        });

        // ── Segment ──────────────────────────────────────────────────────────
        // Clustered on (CameraId, StartUtc) ahead of the M4 date-partitioning work the plan calls
        // for on this table — playback/timeline queries are always "this camera, this time range",
        // so this ordering is right regardless of when partitioning lands.
        builder.Entity<Segment>(e =>
        {
            // 450 chars (900 bytes as nvarchar) is SQL Server's max key size for an index, not an
            // arbitrary choice — comfortably fits a real path (UNC share + camera GUID + timestamp
            // filename is ~90 chars) while staying under that limit for the unique index below.
            e.Property(x => x.FilePath).HasMaxLength(450).IsRequired();
            e.Property(x => x.Codec).HasMaxLength(50);
            e.HasOne(x => x.Camera).WithMany()
                .HasForeignKey(x => x.CameraId).OnDelete(DeleteBehavior.Cascade);
            // Id stays the primary key (uniqueness, FK targets) but is deliberately non-clustered —
            // the clustered index is (CameraId, StartUtc) instead, since every real query here is
            // "this camera, this time range", not "this Id".
            e.HasKey(x => x.Id).IsClustered(false);
            e.HasIndex(x => new { x.CameraId, x.StartUtc }).IsClustered();
            // Backstop against duplicate segment reports (a node restart mid-session re-scanning its
            // output directory with no memory of what it already reported is the scenario the
            // in-session HashSet in RecordingSession can't cover) — NodeService.RecordSegmentsAsync
            // catches the resulting constraint violation and skips the duplicate rather than losing
            // the rest of the batch.
            e.HasIndex(x => x.FilePath).IsUnique();
        });
    }
}
