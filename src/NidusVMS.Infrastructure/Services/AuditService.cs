using NidusVMS.Core.Entities;
using NidusVMS.Core.Interfaces;
using NidusVMS.Infrastructure.Data;

namespace NidusVMS.Infrastructure.Services;

public class AuditService(ApplicationDbContext db) : IAuditService
{
    public async Task LogAsync(string action, string? userId, string? userName, string? ipAddress,
        string? details = null, CancellationToken ct = default)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Action = action,
            UserId = userId,
            UserName = userName,
            IpAddress = ipAddress,
            Details = details,
            OccurredAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }
}
