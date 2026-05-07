namespace AmGatewayCloud.CloudGateway.Services;

public enum WriteErrorKind
{
    Transient,   // 可重试：网络超时、连接断开、DB 暂时不可用
    Permanent    // 不可重试：数据格式违规、约束冲突、消息超大
}

public class WriteException : Exception
{
    public WriteErrorKind Kind { get; }

    public WriteException(string message, WriteErrorKind kind) : base(message)
    {
        Kind = kind;
    }
}
