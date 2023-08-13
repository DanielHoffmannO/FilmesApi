using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Microsoft.EntityFrameworkCore
{
    public interface IRepository<TEntity, TId>
        where TEntity : Entity<TEntity, TId>
        where TId : struct
    {
        void Dispose();

        void Add(TEntity entity);


        TEntity GetById(TId id);

        bool Commit();

        void Update(TEntity entity);

        void Delete(TId id);

        TResultSelect First<TResultSelect, TResult>(Expression<Func<TEntity, TResult>> source, Expression<Func<TResult, bool>> predicate, Expression<Func<TResult, TResultSelect>> result);

        Task<TResultSelect> FirstAsync<TResultSelect, TResult>(Expression<Func<TEntity, TResult>> source, Expression<Func<TResult, bool>> predicate, Expression<Func<TResult, TResultSelect>> result);

        TResult First<TResult>(Expression<Func<TEntity, TResult>> source, Expression<Func<TResult, bool>> predicate);

        Task<TResult> FirstAsync<TResult>(Expression<Func<TEntity, TResult>> source, Expression<Func<TResult, bool>> predicate);

        IEnumerable<TResult> Select<TResult>(Expression<Func<TEntity, TResult>> source, Expression<Func<TResult, bool>> predicate);

    }
}
