using System.Globalization;

namespace SearchQuality.Postgres;

public static class PgVectorLiteral
{
    public static string Format(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Any(value => !float.IsFinite(value)))
        {
            throw new ArgumentException(
                "Vector values must be finite numbers; pgvector rejects NaN and Infinity.",
                nameof(vector));
        }

        return $"[{string.Join(',', vector.Select(value => value.ToString("R", CultureInfo.InvariantCulture)))}]";
    }
}
