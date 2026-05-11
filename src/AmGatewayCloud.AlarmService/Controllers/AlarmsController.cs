using AmGatewayCloud.AlarmDomain.Common;
using AmGatewayCloud.Shared.DTOs;
using AmGatewayCloud.AlarmService.Services;
using Microsoft.AspNetCore.Mvc;

namespace AmGatewayCloud.AlarmService.Controllers;

/// <summary>
/// 报警事件查询、确认、抑制、关闭
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AlarmsController : ControllerBase
{
    private readonly AlarmQueryService _queryService;
    private readonly ILogger<AlarmsController> _logger;

    public AlarmsController(AlarmQueryService queryService, ILogger<AlarmsController> logger)
    {
        _queryService = queryService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<AlarmEventDto>>> GetAlarms(
        [FromQuery] string? factoryId,
        [FromQuery] string? deviceId,
        [FromQuery] string? status,
        [FromQuery] string? level,
        [FromQuery] bool? isStale,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _queryService.GetAlarmsAsync(
            factoryId, deviceId, status, level, isStale, page, pageSize, ct);

        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AlarmEventDto>> GetAlarm(Guid id, CancellationToken ct)
    {
        var result = await _queryService.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("{id:guid}/ack")]
    public async Task<ActionResult<AlarmEventDto>> Acknowledge(Guid id, [FromBody] AckRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AcknowledgedBy))
            return BadRequest("AcknowledgedBy is required");

        try
        {
            var result = await _queryService.AcknowledgeAsync(id, request.AcknowledgedBy, ct);
            if (result is null)
                return NotFound("Alarm not found");

            _logger.LogInformation("Alarm {AlarmId} acknowledged by {User}", id, request.AcknowledgedBy);
            return Ok(result);
        }
        catch (AlarmStateException ex)
        {
            return Conflict(new { ex.Message, ex.CurrentStatus, ex.AttemptedOperation });
        }
    }

    [HttpPost("{id:guid}/suppress")]
    public async Task<ActionResult<AlarmEventDto>> Suppress(Guid id, [FromBody] SuppressRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SuppressedBy))
            return BadRequest("SuppressedBy is required");

        try
        {
            var result = await _queryService.SuppressAsync(id, request.SuppressedBy, request.Reason, ct);
            if (result is null)
                return NotFound("Alarm not found");

            _logger.LogInformation("Alarm {AlarmId} suppressed by {User}: {Reason}", id, request.SuppressedBy, request.Reason);
            return Ok(result);
        }
        catch (AlarmStateException ex)
        {
            return Conflict(new { ex.Message, ex.CurrentStatus, ex.AttemptedOperation });
        }
    }

    [HttpPost("{id:guid}/clear")]
    public async Task<ActionResult<AlarmEventDto>> Clear(Guid id, CancellationToken ct)
    {
        try
        {
            var result = await _queryService.ClearAsync(id, ct);
            if (result is null)
                return NotFound("Alarm not found");

            _logger.LogInformation("Alarm {AlarmId} manually cleared", id);
            return Ok(result);
        }
        catch (AlarmStateException ex)
        {
            return Conflict(new { ex.Message, ex.CurrentStatus, ex.AttemptedOperation });
        }
    }
}
