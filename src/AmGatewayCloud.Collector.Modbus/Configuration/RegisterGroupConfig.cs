namespace AmGatewayCloud.Collector.Modbus.Configuration;

public class RegisterGroupConfig
{
    public string Name { get; set; } = string.Empty;
    public RegisterType Type { get; set; }
    public ushort Start { get; set; }
    public int Count { get; set; }
    /// <summary>
    /// 组级缩放除数，转换公式：value = raw / ScaleFactor + Offset。
    /// 默认 1.0（不缩放）。工业传感器常用 10.0（853 → 85.3）。
    /// </summary>
    public double ScaleFactor { get; set; } = 1.0;

    /// <summary>
    /// 组级缩放除数（优先级低于 TagScales，高于 ScaleFactor 兼容逻辑）。
    /// 转换公式同上：value = raw / Scale + Offset。Scale 也是除数语义，不是乘数。
    /// </summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// 偏移量，转换公式：value = raw / Scale + Offset。默认 0.0。
    /// </summary>
    public double Offset { get; set; } = 0.0;
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Per-tag 缩放除数覆盖。Key = tag 名, Value = 除数（语义同 Scale/ScaleFactor）。
    /// 优先级：TagScales[tag] > Scale > ScaleFactor。
    /// </summary>
    public Dictionary<string, double> TagScales { get; set; } = [];
}
