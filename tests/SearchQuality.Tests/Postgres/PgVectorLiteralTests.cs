using SearchQuality.Postgres;

namespace SearchQuality.Tests.Postgres;

public sealed class PgVectorLiteralTests
{
    [Fact]
    public void Format_uses_invariant_decimal_separator()
    {
        var value = PgVectorLiteral.Format([0.5f, -1.25f, 3f]);

        Assert.Equal("[0.5,-1.25,3]", value);
    }
}
