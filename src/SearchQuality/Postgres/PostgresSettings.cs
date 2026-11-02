namespace SearchQuality.Postgres;

public sealed record PostgresSettings(string ConnectionString, string Schema)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}

public sealed record PostgresHealth(bool CanConnect, bool VectorExtensionInstalled, string? Error);

public sealed record PostgresIndexStats(bool Available, int DocumentCount, int ChunkCount, string? Error);
