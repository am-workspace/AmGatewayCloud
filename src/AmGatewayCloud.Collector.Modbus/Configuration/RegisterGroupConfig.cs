namespace AmGatewayCloud.Collector.Modbus.Configuration;

public class RegisterGroupConfig
{
    public string Name { get; set; } = string.Empty;
    public RegisterType Type { get; set; }
    public ushort Start { get; set; }
    public int Count { get; set; }
    public double ScaleFactor { get; set; } = 1.0;
    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; } = 0.0;
    public List<string> Tags { get; set; } = [];
}
