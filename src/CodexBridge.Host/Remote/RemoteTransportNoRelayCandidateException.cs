namespace CodexBridge.Host.Remote;

public sealed class RemoteTransportNoRelayCandidateException : Exception
{
    public RemoteTransportNoRelayCandidateException()
        : base("transport_answer_no_relay_candidate") { }
}
