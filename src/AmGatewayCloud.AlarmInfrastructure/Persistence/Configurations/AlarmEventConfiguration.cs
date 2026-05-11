using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AmGatewayCloud.AlarmInfrastructure.Persistence.Configurations;

public class AlarmEventConfiguration : IEntityTypeConfiguration<AlarmEventEntity>
{
    public void Configure(EntityTypeBuilder<AlarmEventEntity> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.RuleId).IsRequired();
        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.FactoryId).IsRequired();
        builder.Property(e => e.DeviceId).IsRequired();
        builder.Property(e => e.Tag).IsRequired();
        builder.Property(e => e.Level).IsRequired().HasMaxLength(20);
        builder.Property(e => e.Status).IsRequired().HasMaxLength(20);
        builder.Property(e => e.TriggeredAt).IsRequired();

        // 索引 — 与现有 init-db.sql 中的索引一致
        builder.HasIndex(e => new { e.TenantId, e.FactoryId, e.DeviceId, e.TriggeredAt })
            .HasDatabaseName("idx_alarm_events_lookup");

        builder.HasIndex(e => new { e.Status, e.TriggeredAt })
            .HasDatabaseName("idx_alarm_events_status")
            .HasFilter("status IN ('Active', 'Acked', 'Suppressed')");

        builder.HasIndex(e => new { e.RuleId, e.DeviceId, e.Status })
            .HasDatabaseName("idx_alarm_events_rule_device")
            .HasFilter("status IN ('Active', 'Acked')");

        // 唯一索引：同一 (rule_id, device_id) 只允许一条 Active/Acked 报警
        builder.HasIndex(e => new { e.RuleId, e.DeviceId })
            .IsUnique()
            .HasDatabaseName("idx_alarm_events_active_unique")
            .HasFilter("status IN ('Active', 'Acked')");

        // 导航属性：FK → alarm_rules
        builder.HasOne(e => e.Rule)
            .WithMany()
            .HasForeignKey(e => e.RuleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
