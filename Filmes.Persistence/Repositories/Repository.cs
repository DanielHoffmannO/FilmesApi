using System.Linq.Expressions;

namespace Microsoft.EntityFrameworkCore;

public abstract class Repository<TEntity, TId> : IRepository<TEntity, TId>
    where TEntity : Entity<TEntity, TId>
    where TId : struct
{
    protected AbstractContext Db { get; }
    protected DbSet<TEntity> DbSet { get; }

    protected Repository(AbstractContext context)
    {
        Db = context ?? throw new ArgumentException("Failed to initialize the repository. Invalid context.", nameof(context));
        DbSet = Db.Set<TEntity>();
    }

    public virtual void Dispose() =>
        Db.Dispose();

    public virtual void Add(TEntity entity)
    {
        Db.Entry(entity).State = EntityState.Added;
        DbSet.Add(entity);
    }

    public virtual TEntity GetById(TId id) =>
        DbSet.Find(id);

    public virtual bool Commit() =>
        Db.Commit();

    public virtual void Update(TEntity entity)
    {
        Db.Entry(entity).State = EntityState.Modified;
        DbSet.Update(entity);
    }

    public virtual void Delete(TId id)
    {
        TEntity val = DbSet.Find(id);
        if (val != null)
            Db.Entry(val).State = EntityState.Deleted;
    }

    public virtual bool Any<TResult>(Expression<Func<TEntity, TResult>> source, Expression<Func<TResult, bool>> predicate) =>
        !EqualityComparer<TResult>.Default.Equals(First(source, predicate), default);

    public virtual TResultSelect First<TResultSelect, TResult>(Expression<Func<TEntity, TResult>> source, Expression<Func<TResult, bool>> predicate, Expression<Func<TResult, TResultSelect>> result) =>
        DbSet.Select(source).Where(predicate).Select(result).FirstOrDefault();

    public virtual Task<TResultSelect> FirstAsync<TResultSelect, TResult>(Expression<Func<TEntity, TResult>> source, Expression<Func<TResult, bool>> predicate, Expression<Func<TResult, TResultSelect>> result) =>
        DbSet.Select(source).Where(predicate).Select(result).FirstOrDefaultAsync();

    public virtual TResult First<TResult>(Expression<Func<TEntity, TResult>> source, Expression<Func<TResult, bool>> predicate) =>
        DbSet.Select(source).FirstOrDefault(predicate);

    public virtual Task<TResult> FirstAsync<TResult>(Expression<Func<TEntity, TResult>> source, Expression<Func<TResult, bool>> predicate) =>
        DbSet.Select(source).FirstOrDefaultAsync(predicate);

    public virtual IEnumerable<TResult> Select<TResult>(Expression<Func<TEntity, TResult>> source, Expression<Func<TResult, bool>> predicate) =>
        DbSet.Select(source).Where(predicate).ToArray();
}