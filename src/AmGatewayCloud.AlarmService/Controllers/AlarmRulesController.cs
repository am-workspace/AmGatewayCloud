using AmGatewayCloud.Shared.DTOs;
using AmGatewayCloud.AlarmService.Services;
using Microsoft.AspNetCore.Mvc;

namespace AmGatewayCloud.AlarmService.Controllers;

/// <summary>
/// 报警规则 CRUD
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AlarmRulesController : ControllerBase
{
    private readonly AlarmRuleService _ruleService;
    private readonly ILogger<AlarmRulesController> _logger;

    public AlarmRulesController(AlarmRuleService ruleService, ILogger<AlarmRulesController> logger)
    {
        _ruleService = ruleService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<List<AlarmRuleDto>>> GetRules(
        [FromQuery] string? factoryId,
        [FromQuery] string? tag,
        CancellationToken ct)
    {
        var rules = await _ruleService.GetRulesAsync(factoryId, tag, ct);
        return Ok(rules);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<AlarmRuleDto>> GetRule(string id, CancellationToken ct)
    {
        var rule = await _ruleService.GetByIdAsync(id, ct);
        return rule is null ? NotFound() : Ok(rule);
    }

    [HttpPost]
    public async Task<ActionResult<AlarmRuleDto>> CreateRule([FromBody] CreateAlarmRuleRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            return BadRequest("Rule Id is required");
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Rule Name is required");
        if (string.IsNullOrWhiteSpace(request.Tag))
            return BadRequest("Tag is required");

        var (rule, error) = await _ruleService.CreateRuleAsync(request, ct);
        if (error is not null)
            return BadRequest(error);

        _logger.LogInformation("Alarm rule {RuleId} created", request.Id);
        return CreatedAtAction(nameof(GetRule), new { id = rule!.Id }, rule);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<AlarmRuleDto>> UpdateRule(string id, [FromBody] UpdateAlarmRuleRequest request, CancellationToken ct)
    {
        var (rule, error) = await _ruleService.UpdateRuleAsync(id, request, ct);
        if (error is not null)
            return error.Contains("not found") ? NotFound(error) : BadRequest(error);

        _logger.LogInformation("Alarm rule {RuleId} updated", id);
        return Ok(rule);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteRule(string id, CancellationToken ct)
    {
        var (success, error) = await _ruleService.DeleteRuleAsync(id, ct);
        if (!success)
            return error!.Contains("not found") ? NotFound(error) : BadRequest(error);

        _logger.LogInformation("Alarm rule {RuleId} deleted", id);
        return NoContent();
    }
}
