using Microsoft.EntityFrameworkCore;

namespace TechEquipments
{
    /// <summary>
    /// Отдельный DbContext для служебной БД srd_db:
    /// Info, shared libraries, favorites, PDF view state.
    /// </summary>
    public sealed class PgInfoDbContext : DbContext
    {
        public PgInfoDbContext(DbContextOptions<PgInfoDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("public");
            base.OnModelCreating(modelBuilder);
        }
    }
}