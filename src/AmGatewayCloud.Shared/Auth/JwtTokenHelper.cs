using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace AmGatewayCloud.Shared.Auth;

/// <summary>
/// 开发/测试用 JWT Token 生成工具
/// 生产环境应使用正式的 Identity Provider（Keycloak/Auth0 等）
/// </summary>
public static class JwtTokenHelper
{
    /// <summary>
    /// 生成 JWT Token
    /// </summary>
    /// <param name="secret">签名密钥（至少 16 字符）</param>
    /// <param name="tenantId">租户 ID</param>
    /// <param name="userId">用户 ID</param>
    /// <param name="role">角色</param>
    /// <param name="name">用户名</param>
    /// <param name="expiresIn">过期时间（默认 24 小时）</param>
    public static string GenerateToken(
        string secret,
        string tenantId,
        string userId,
        string role = "Admin",
        string name = "dev-user",
        TimeSpan? expiresIn = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new("tenant_id", tenantId),
            new(ClaimTypes.Role, role),
            new(ClaimTypes.Name, name),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: "amgateway",
            audience: "amgateway-api",
            claims: claims,
            expires: DateTime.UtcNow.Add(expiresIn ?? TimeSpan.FromHours(24)),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
