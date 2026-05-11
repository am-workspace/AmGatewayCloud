using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AmGatewayCloud.AlarmInfrastructure.Persistence.Configurations;

public class AlarmRuleConfiguration : IEntityTypeConfiguration<AlarmRuleEntity>
{
    public void Configure(EntityTypeBuilder<AlarmRuleEntity> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Name).IsRequired();
        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.Tag).IsRequired();
        builder.Property(r => r.Operator).IsRequired().HasMaxLength(5);
        builder.Property(r => r.Level).IsRequired().HasMaxLength(20);

        // 索引 — 与现有 init-db.sql 中的索引一致
        builder.HasIndex(r => new { r.Tag, r.Enabled })
            .HasDatabaseName("idx_alarm_rules_tag")
            .HasFilter("enabled = TRUE");

        builder.HasIndex(r => new { r.TenantId, r.FactoryId, r.DeviceId })
            .HasDatabaseName("idx_alarm_rules_scope");
    }
}
