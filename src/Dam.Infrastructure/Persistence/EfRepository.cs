using Dam.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dam.Infrastructure.Persistence;

public sealed class EfRepository<T>(DamDbContext db) : IRepository<T> where T : class
{
    public IQueryable<T> Query() => db.Set<T>();
    public async Task<T?> FindAsync(Guid id, CancellationToken ct) => await db.Set<T>().FindAsync([id], ct);
    public void Add(T entity) => db.Set<T>().Add(entity);
    public void AddRange(IEnumerable<T> entities) => db.Set<T>().AddRange(entities);
    public void Remove(T entity) => db.Set<T>().Remove(entity);
    public void RemoveRange(IEnumerable<T> entities) => db.Set<T>().RemoveRange(entities);
}

public sealed class EfQueryExecutor : IQueryExecutor
{
    public Task<List<T>> ToListAsync<T>(IQueryable<T> query, CancellationToken ct) => query.ToListAsync(ct);
    public Task<T?> FirstOrDefaultAsync<T>(IQueryable<T> query, CancellationToken ct) => query.FirstOrDefaultAsync(ct);
    public Task<bool> AnyAsync<T>(IQueryable<T> query, CancellationToken ct) => query.AnyAsync(ct);
    public Task<int> CountAsync<T>(IQueryable<T> query, CancellationToken ct) => query.CountAsync(ct);
}
