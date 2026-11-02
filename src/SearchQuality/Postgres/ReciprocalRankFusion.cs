using SearchQuality.Domain;

namespace SearchQuality.Postgres;

public static class ReciprocalRankFusion
{
    public const int DefaultRankConstant = 60;

    public static IReadOnlyList<SearchHit> Fuse(
        IReadOnlyList<SearchHit> lexicalHits,
        IReadOnlyList<SearchHit> vectorHits,
        int topK,
        int rankConstant = DefaultRankConstant)
    {
        ArgumentNullException.ThrowIfNull(lexicalHits);
        ArgumentNullException.ThrowIfNull(vectorHits);
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rankConstant, 1);

        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        AddRanking(candidates, lexicalHits, rankConstant, isLexical: true);
        AddRanking(candidates, vectorHits, rankConstant, isLexical: false);

        return candidates.Values
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.BestRank)
            .ThenBy(candidate => candidate.Hit.DocumentId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Hit.ChunkId, StringComparer.Ordinal)
            .Take(topK)
            .Select(candidate => candidate.Hit with
            {
                Score = candidate.Score,
                HybridRanks = new HybridRankDetails(
                    candidate.FullTextRank,
                    candidate.VectorRank,
                    rankConstant)
            })
            .ToArray();
    }

    private static void AddRanking(
        IDictionary<string, Candidate> candidates,
        IReadOnlyList<SearchHit> hits,
        int rankConstant,
        bool isLexical)
    {
        for (var index = 0; index < hits.Count; index++)
        {
            var hit = hits[index];
            var rank = index + 1;
            var contribution = 1d / (rankConstant + rank);

            if (candidates.TryGetValue(hit.ChunkId, out var existing))
            {
                candidates[hit.ChunkId] = existing with
                {
                    Score = existing.Score + contribution,
                    BestRank = Math.Min(existing.BestRank, rank),
                    FullTextRank = isLexical ? rank : existing.FullTextRank,
                    VectorRank = isLexical ? existing.VectorRank : rank
                };
            }
            else
            {
                candidates[hit.ChunkId] = new Candidate(
                    hit,
                    contribution,
                    rank,
                    isLexical ? rank : null,
                    isLexical ? null : rank);
            }
        }
    }

    private sealed record Candidate(
        SearchHit Hit,
        double Score,
        int BestRank,
        int? FullTextRank,
        int? VectorRank);
}
