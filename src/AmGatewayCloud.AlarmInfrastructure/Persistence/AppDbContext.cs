using AmGatewayCloud.AlarmInfrastructure.Persistence.Configurations;
using AmGatewayCloud.Shared.Tenant;
using Microsoft.EntityFrameworkCore;

namespace AmGatewayCloud.AlarmInfrastructure.Persistence;

public class AppDbContext : DbContext
{
    private readonly string _tenantId;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext) : base(options)
    {
        _tenantId = tenantContext.TenantId;
    }

    public DbSet<AlarmEventEntity> AlarmEvents => Set<AlarmEventEntity>();
    public DbSet<AlarmRuleEntity> AlarmRules => Set<AlarmRuleEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new AlarmEventConfiguration());
        modelBuilder.ApplyConfiguration(new AlarmRuleConfiguration());

        // Global Query Filter — 自动按租户过滤
        modelBuilder.Entity<AlarmEventEntity>()
            .HasQueryFilter(e => e.TenantId == _tenantId);
        modelBuilder.Entity<AlarmRuleEntity>()
            .HasQueryFilter(e => e.TenantId == _tenantId);

        // 使用现有数据库的 schema，不自动创建表
        modelBuilder.HasDefaultSchema("public");
    }
}
