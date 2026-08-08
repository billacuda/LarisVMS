using Microsoft.EntityFrameworkCore;
using Rcordr.Core.Interfaces;
using Rcordr.Infrastructure.Data;

namespace Rcordr.Infrastructure.Repositories;

public class Repository<T>(ApplicationDbContext db) : IRepository<T> where T : class
{
    public async Task<T?> GetByIdAsync(object id, CancellationToken ct = default)
        => await db.Set<T>().FindAsync([id], ct);

    public async Task<List<T>> GetAllAsync(CancellationToken ct = default)
        => await db.Set<T>().AsNoTracking().ToListAsync(ct);

    public async Task AddAsync(T entity, CancellationToken ct = default)
        => await db.Set<T>().AddAsync(entity, ct);

    public void Update(T entity) => db.Set<T>().Update(entity);

    public void Remove(T entity) => db.Set<T>().Remove(entity);

    public Task<int> SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
