using SearchQuality.Domain;
using SearchQuality.Postgres;

namespace SearchQuality.Tests.Postgres;

public sealed class ReciprocalRankFusionTests
{
    [Fact]
    public void Fuse_rewards_chunks_that_rank_highly_in_both_lists()
    {
        var lexical = new[]
        {
            Hit("a", 9),
            Hit("b", 8),
            Hit("c", 7)
        };
        var vector = new[]
        {
            Hit("c", 0.9),
            Hit("a", 0.8),
            Hit("d", 0.7)
        };

        var fused = ReciprocalRankFusion.Fuse(lexical, vector, topK: 3);

        Assert.Equal(["a", "c", "b"], fused.Select(hit => hit.ChunkId));
        Assert.True(fused[0].Score > fused[1].Score);
        Assert.Equal(
            (1d / 61) + (1d / 62),
            fused[0].Score,
            precision: 12);
        Assert.Equal(1, fused[0].HybridRanks?.FullTextRank);
        Assert.Equal(2, fused[0].HybridRanks?.VectorRank);
        Assert.Equal(60, fused[0].HybridRanks?.RankConstant);
    }

    [Fact]
    public void Fuse_uses_rank_instead_of_incompatible_source_scores()
    {
        var lexical = new[] { Hit("lexical-first", 1000) };
        var vector = new[] { Hit("vector-first", 0.001) };

        var fused = ReciprocalRankFusion.Fuse(lexical, vector, topK: 2);

        Assert.Equal(fused[0].Score, fused[1].Score, precision: 12);
        Assert.Equal("lexical-first", fused[0].ChunkId);
    }

    private static SearchHit Hit(string id, double score) =>
        new(id, id, id, $"Text for {id}", score);
}
