using AmGatewayCloud.Shared.DTOs;
using AmGatewayCloud.WorkOrderService.Services;
using Microsoft.AspNetCore.Mvc;

namespace AmGatewayCloud.WorkOrderService.Controllers;

[ApiController]
[Route("api/workorders")]
public class WorkOrdersController : ControllerBase
{
    private readonly WorkOrderQueryService _queryService;
    private readonly ILogger<WorkOrdersController> _logger;

    public WorkOrdersController(WorkOrderQueryService queryService, ILogger<WorkOrdersController> logger)
    {
        _queryService = queryService;
        _logger = logger;
    }

    /// <summary>
    /// 分页查询工单
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<WorkOrderDto>>> GetWorkOrders(
        [FromQuery] string? factoryId,
        [FromQuery] string? status,
        [FromQuery] string? assignee,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var result = await _queryService.QueryAsync(factoryId, status, assignee, page, pageSize, ct);
        return Ok(result);
    }

    /// <summary>
    /// 查询单个工单
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WorkOrderDto>> GetWorkOrder(Guid id, CancellationToken ct)
    {
        var result = await _queryService.GetByIdAsync(id, ct);
        if (result is null) return NotFound();
        return Ok(result);
    }

    /// <summary>
    /// 分配工单（Pending → InProgress）
    /// </summary>
    [HttpPost("{id:guid}/assign")]
    public async Task<ActionResult<WorkOrderDto>> AssignWorkOrder(
        Guid id, [FromBody] AssignWorkOrderRequest request, CancellationToken ct)
    {
        var result = await _queryService.AssignAsync(id, request.Assignee, ct);
        if (result is null) return BadRequest(new { message = "工单不存在或状态不允许分配" });
        return Ok(result);
    }

    /// <summary>
    /// 完成工单（InProgress → Completed）
    /// </summary>
    [HttpPost("{id:guid}/complete")]
    public async Task<ActionResult<WorkOrderDto>> CompleteWorkOrder(
        Guid id, [FromBody] CompleteWorkOrderRequest request, CancellationToken ct)
    {
        var result = await _queryService.CompleteAsync(id, request.CompletedBy, request.CompletionNote, ct);
        if (result is null) return BadRequest(new { message = "工单不存在或状态不允许完成" });
        return Ok(result);
    }

    /// <summary>
    /// 工单状态汇总
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<WorkOrderSummary>> GetSummary(
        [FromQuery] string? factoryId, CancellationToken ct)
    {
        var result = await _queryService.GetSummaryAsync(factoryId, ct);
        return Ok(result);
    }
}
