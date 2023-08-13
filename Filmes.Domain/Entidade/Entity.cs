
namespace Microsoft.EntityFrameworkCore
{
    public abstract class Entity<TEntity, TId> where TEntity : Entity<TEntity, TId> where TId : struct
    {
        public TId Id { get; protected set; }


        public override bool Equals(object obj)
        {
            TEntity val = obj as TEntity;
            if ((object)this == val)
            {
                return true;
            }

            if (val != null)
            {
                return Id.Equals(val.Id);
            }

            return false;
        }

        public static bool operator ==(Entity<TEntity, TId> a, Entity<TEntity, TId> b)
        {
            if ((object)a == null && (object)b == null)
            {
                return true;
            }

            if ((object)a == null || (object)b == null)
            {
                return false;
            }

            return a.Equals(b);
        }

        public static bool operator !=(Entity<TEntity, TId> a, Entity<TEntity, TId> b)
        {
            return !(a == b);
        }

        public override int GetHashCode()
        {
            return GetType().GetHashCode() * 907 + Id.GetHashCode();
        }

        public override string ToString()
        {
            return GetType().Name + "[Id=" + Id.ToString() + "]";
        }
    }
}

