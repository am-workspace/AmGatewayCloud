using AmGatewayCloud.CloudGateway.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AmGatewayCloud.CloudGateway.Infrastructure;

public class NpgsqlConnectionFactory
{
    private readonly string _connectionString;

    public NpgsqlConnectionFactory(IOptions<CloudGatewayConfig> options, string databaseName)
    {
        var config = options.Value;

        string host;
        int port;
        string username;
        string password;
        string sslMode;

        if (databaseName == config.TimescaleDb.Database)
        {
            host = config.TimescaleDb.Host;
            port = config.TimescaleDb.Port;
            username = config.TimescaleDb.Username;
            password = config.TimescaleDb.Password;
            sslMode = config.TimescaleDb.SslMode;
        }
        else
        {
            host = config.PostgreSql.Host;
            port = config.PostgreSql.Port;
            username = config.PostgreSql.Username;
            password = config.PostgreSql.Password;
            sslMode = config.PostgreSql.SslMode;
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = databaseName,
            Username = username,
            Password = password,
            SslMode = Enum.TryParse<SslMode>(sslMode, true, out var ssl) ? ssl : SslMode.Require,
            MaxPoolSize = 20,
            ConnectionIdleLifetime = 300
        };

        _connectionString = builder.ConnectionString;
    }

    public NpgsqlConnection CreateConnection() => new(_connectionString);
}
