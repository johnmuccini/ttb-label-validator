namespace Ttb.LabelWeb;

public sealed class ApiKeyStore
{
    readonly object gate = new();
    string? apiKey;

    public bool HasValidatedKey { get { lock (gate) return apiKey is not null; } }
    public void Set(string value) { lock (gate) apiKey = value; }
    public string? Get() { lock (gate) return apiKey; }
}
