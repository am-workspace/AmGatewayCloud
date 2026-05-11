using AmGatewayCloud.AlarmDomain.Aggregates.Alarm;
using AmGatewayCloud.AlarmInfrastructure.Persistence;
using AmGatewayCloud.AlarmInfrastructure.Repositories;

namespace AmGatewayCloud.AlarmService.Infrastructure;

/// <summary>
/// 数据库初始化器：使用 EF Core EnsureCreated 确保表结构，并插入种子规则数据
/// </summary>
public class AlarmDbInitializer
{
    private readonly AppDbContext _dbContext;
    private readonly AlarmRuleRepository _ruleRepo;
    private readonly ILogger<AlarmDbInitializer> _logger;

    public AlarmDbInitializer(
        AppDbContext dbContext,
        AlarmRuleRepository ruleRepo,
        ILogger<AlarmDbInitializer> logger)
    {
        _dbContext = dbContext;
        _ruleRepo = ruleRepo;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await SeedRulesAsync(ct);
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        // EF Core EnsureCreated：根据 Configuration 映射自动创建表和索引
        await _dbContext.Database.EnsureCreatedAsync(ct);
        _logger.LogInformation("Database schema verified (EF Core EnsureCreated)");
    }

    private async Task SeedRulesAsync(CancellationToken ct)
    {
        var seedRules = GetSeedRules();
        int inserted = 0;

        foreach (var rule in seedRules)
        {
            var rows = await _ruleRepo.InsertIfNotExistsAsync(rule, ct);
            inserted += rows;
        }

        _logger.LogInformation("AlarmDbInitializer: {Inserted} seed rules inserted ({Total} already existed)",
            inserted, seedRules.Count - inserted);
    }

    private static List<AlarmRule> GetSeedRules() =>
    [
        new("high-temp-warning", "高温警告", "default", null, null, "temperature", OperatorType.GreaterThan, 28, null, 26, AlarmLevel.Warning, 5, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("high-temp-critical", "高温严重", "default", null, null, "temperature", OperatorType.GreaterThan, 35, null, 30, AlarmLevel.Critical, 5, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("low-temp-warning", "低温警告", "default", null, null, "temperature", OperatorType.LessThan, 18, null, 20, AlarmLevel.Warning, 5, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("high-pressure-warning", "高压警告", "default", null, null, "pressure", OperatorType.GreaterThan, 115, null, 110, AlarmLevel.Warning, 5, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("low-pressure-warning", "低压警告", "default", null, null, "pressure", OperatorType.LessThan, 85, null, 90, AlarmLevel.Warning, 5, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("high-level-warning", "液位过高", "default", null, null, "level", OperatorType.GreaterThan, 90, null, 85, AlarmLevel.Warning, 5, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("high-level-critical", "液位严重", "default", null, null, "level", OperatorType.GreaterThan, 95, null, 90, AlarmLevel.Critical, 5, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("low-level-warning", "液位过低", "default", null, null, "level", OperatorType.LessThan, 10, null, 15, AlarmLevel.Warning, 5, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("high-voltage-warning", "电压过高", "default", null, null, "voltage", OperatorType.GreaterThan, 395, null, 390, AlarmLevel.Warning, 5, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("low-voltage-warning", "电压过低", "default", null, null, "voltage", OperatorType.LessThan, 360, null, 365, AlarmLevel.Warning, 5, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("high-current-warning", "电流过大", "default", null, null, "current", OperatorType.GreaterThan, 18, null, 15, AlarmLevel.Warning, 5, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("high-rpm-warning", "转速过高", "default", null, null, "rpm", OperatorType.GreaterThan, 1650, null, 1600, AlarmLevel.Warning, 5, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("high-humidity-warning", "湿度过高", "default", null, null, "humidity", OperatorType.GreaterThan, 85, null, 80, AlarmLevel.Warning, 5, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("freq-abnormal-warning", "频率异常", "default", null, null, "frequency", OperatorType.GreaterThan, 50.8, null, 50.5, AlarmLevel.Warning, 5, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("device-alarm", "设备报警", "default", null, null, "diAlarm", OperatorType.Equal, 1, null, 0, AlarmLevel.Warning, 2, 0, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
        new("quality-bad", "数据质量差", "default", null, null, "quality", OperatorType.Equal, 0, "Bad", null, AlarmLevel.Warning, 10, 0, true, "quality == Bad", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
    ];
}
