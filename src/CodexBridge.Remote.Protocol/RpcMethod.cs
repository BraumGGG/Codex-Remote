namespace CodexBridge.Remote.Protocol;

public enum RpcMethod : byte
{
    Status = 1,
    Capabilities = 2,
    Projects = 3,
    Threads = 4,
    Events = 5,
    Subscribe = 6,
    Unsubscribe = 7,
    Image = 8,
    TextFile = 9,
    SubmitText = 10,
    CancelTransfer = 11,
    EventText = 12,
}
