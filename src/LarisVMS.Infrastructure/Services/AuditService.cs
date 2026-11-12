using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

public class AuditService(ApplicationDbContext db) : IAuditService
{
    public async Task LogAsync(string action, string? userId, string? userName, string? ipAddress,
        string? details = null, CancellationToken ct = default)
    {
        // Chained onto the entry before it (AuditChainStore); saves anything else pending in this
        // context too, as it always has.
        await AuditChainStore.AppendAsync(db, new AuditLog
        {
            Action = action,
            UserId = userId,
            UserName = userName,
            IpAddress = ipAddress,
            Details = details,
            OccurredAt = DateTime.UtcNow
        }, ct);
    }
}
