using Microsoft.EntityFrameworkCore;

namespace TechEquipments
{
    public class PgDbContext : DbContext
    {
        public PgDbContext(DbContextOptions<PgDbContext> options) : base(options) { }

        public DbSet<OperatorAct> OperatorActs => Set<OperatorAct>();
        public DbSet<alarm_history> AlarmHistories => Set<alarm_history>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("public");

            modelBuilder.Entity<OperatorAct>(e =>
            {
                e.ToTable("OperatorAct");                 
                e.HasKey(x => new { x.Date, x.Hash });    
            });

            modelBuilder.Entity<alarm_history>(e =>
            {
                e.ToTable("alarm_history");               // имя таблицы как в классе:contentReference[oaicite:3]{index=3}
                e.HasKey(x => x.id);
            });

            base.OnModelCreating(modelBuilder);
        }
    }
}
