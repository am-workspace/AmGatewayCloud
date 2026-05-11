namespace AmGatewayCloud.AlarmDomain.Common;

/// <summary>
/// 报警状态流转不合法时抛出的领域异常
/// </summary>
public class AlarmStateException : InvalidOperationException
{
    public string CurrentStatus { get; }
    public string AttemptedOperation { get; }

    public AlarmStateException(string currentStatus, string attemptedOperation, string message)
        : base(message)
    {
        CurrentStatus = currentStatus;
        AttemptedOperation = attemptedOperation;
    }
}
