using System.Security.Claims;
using AmGatewayCloud.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AmGatewayCloud.WebApi.Controllers;

/// <summary>
/// 认证控制器（开发阶段使用，生产环境应替换为正式 IdP）
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IConfiguration config, ILogger<AuthController> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// 标准登录（开发阶段：验证用户名密码，返回 JWT）
    /// 生产环境应替换为正式的 Identity Provider
    /// </summary>
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        // 开发阶段：任意用户名 + 密码 "admin" 即可登录
        // 生产环境：查询数据库验证用户
        if (string.IsNullOrEmpty(request.Username) || request.Password != "admin")
        {
            return Unauthorized(new { message = "用户名或密码错误" });
        }

        var tenantId = request.TenantId ?? "default";
        var secret = _config["Jwt:Secret"]!;
        var token = JwtTokenHelper.GenerateToken(secret, tenantId, request.Username, "Admin", request.Username);

        _logger.LogInformation("User {Username} logged in, tenant: {TenantId}", request.Username, tenantId);

        return Ok(new LoginResponse
        {
            Token = token,
            TenantId = tenantId,
            UserId = request.Username,
            Name = request.Username,
            Role = "Admin"
        });
    }

    /// <summary>
    /// 开发模式：直接指定租户获取 Token（仅开发环境）
    /// </summary>
    [HttpPost("dev-token")]
    [AllowAnonymous]
    public IActionResult DevToken([FromBody] DevTokenRequest request)
    {
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        if (env != "Development")
        {
            return NotFound();
        }

        var secret = _config["Jwt:Secret"]!;
        var token = JwtTokenHelper.GenerateToken(
            secret, request.TenantId ?? "default", request.UserId ?? "dev-user", request.Role ?? "Admin", request.UserId ?? "dev-user");

        return Ok(new LoginResponse
        {
            Token = token,
            TenantId = request.TenantId ?? "default",
            UserId = request.UserId ?? "dev-user",
            Name = request.UserId ?? "dev-user",
            Role = request.Role ?? "Admin"
        });
    }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? TenantId { get; set; }
}

public class DevTokenRequest
{
    public string? TenantId { get; set; }
    public string? UserId { get; set; }
    public string? Role { get; set; }
}

public class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}
