namespace SearchQuality.Chat.Observability;

/// <summary>Only the Chat host opts in; secrets never enter the public settings API.</summary>
public sealed record ChatTelemetryOptions
{
    public bool Enabled { get; init; }
    public bool CaptureContent { get; init; }
    public Uri? Endpoint { get; init; }
    public string ApiKey { get; init; } = "";
    public string Project { get; init; } = "chat-studio";
    public int MaxContentCharacters { get; init; } = 2_000_000;

    public override string ToString() => $"ChatTelemetryOptions {{ Enabled = {Enabled}, CaptureContent = {CaptureContent}, Project = {Project} }}";

    public static ChatTelemetryOptions FromEnvironment()
    {
        string? Read(string name) => Environment.GetEnvironmentVariable("CHAT_TELEMETRY_" + name);
        if (!bool.TryParse(Read("ENABLED"), out var enabled) || !enabled)
            return new();

        var keyFile = Read("API_KEY_FILE");
        var options = new ChatTelemetryOptions
        {
            Enabled = true,
            CaptureContent = bool.TryParse(Read("CAPTURE_CONTENT"), out var capture) && capture,
            Endpoint = Uri.TryCreate(Read("ENDPOINT"), UriKind.Absolute, out var endpoint) ? endpoint : null,
            ApiKey = string.IsNullOrWhiteSpace(keyFile) ? Read("API_KEY") ?? "" : File.ReadAllText(keyFile).Trim(),
            Project = Read("PROJECT") ?? "chat-studio",
            MaxContentCharacters = int.TryParse(Read("MAX_CONTENT_CHARACTERS"), out var maximum) ? maximum : 2_000_000
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (!Enabled) return;
        if (Endpoint is null || !Endpoint.IsAbsoluteUri || Endpoint.UserInfo.Length != 0
            || Endpoint.Query.Length != 0 || Endpoint.Fragment.Length != 0
            || Endpoint.AbsolutePath != "/v1/traces"
            || (Endpoint.Scheme != "https" && !(Endpoint.Scheme == "http" && Endpoint.IsLoopback)))
            throw new InvalidOperationException("CHAT_TELEMETRY_ENDPOINT must be an HTTPS or loopback HTTP /v1/traces URL without credentials or query parameters.");
        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Any(char.IsWhiteSpace) || ApiKey.Contains(','))
            throw new InvalidOperationException("Authenticated telemetry requires CHAT_TELEMETRY_API_KEY_FILE or CHAT_TELEMETRY_API_KEY.");
        if (string.IsNullOrWhiteSpace(Project) || Project.Length > 100 || Project.Any(char.IsControl))
            throw new InvalidOperationException("CHAT_TELEMETRY_PROJECT must contain 1–100 printable characters.");
        if (MaxContentCharacters is < 1024 or > 4_000_000)
            throw new InvalidOperationException("CHAT_TELEMETRY_MAX_CONTENT_CHARACTERS must be between 1024 and 4000000.");
    }
}
