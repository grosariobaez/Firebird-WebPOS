namespace JomliaWebPosSync.Resilience;

// Only controlled codes/field names are safe for logs and ULTIMO_ERROR.
public sealed class SyncException(string code, string field, bool transient = false) : Exception(code)
{
    public string Code { get; } = code;
    public string Field { get; } = field;
    public bool Transient { get; } = transient;
}
