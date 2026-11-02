using SearchQuality.Chat;

namespace SearchQuality.Chat.Tests;

public sealed class DiaryConfirmationLogTests
{
    private static DiaryConfirmation Recorded(
        string kind, string label, long id = 1) =>
        new(DiaryConfirmationStatus.Recorded, kind, label, id);

    [Fact]
    public void Successful_retry_clears_only_the_matching_failure()
    {
        var log = new DiaryConfirmationLog();
        log.ReportFailure("упражнение", "Seated cable row", "дубликат");
        log.ReportSuccess(Recorded("упражнение", "Seated cable row"));

        var snapshot = log.Snapshot();

        Assert.Single(snapshot);
        Assert.Equal(DiaryConfirmationStatus.Recorded, snapshot[0].Status);
    }

    [Fact]
    public void Unrelated_success_keeps_the_failure_visible()
    {
        var log = new DiaryConfirmationLog();
        log.ReportSuccess(Recorded("упражнение", "Seated cable row"));
        log.ReportFailure("тренировка", "Day 2 — Upper + Core", "без изричен край");
        log.ReportSuccess(Recorded("хранене", "2-ро хранене", id: 2));

        var snapshot = log.Snapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Contains(snapshot, entry =>
            entry.Status == DiaryConfirmationStatus.Rejected
            && entry.Label == "Day 2 — Upper + Core");
    }

    [Fact]
    public void Generic_failure_is_cleared_by_any_following_success()
    {
        var log = new DiaryConfirmationLog();
        log.ReportFailure(null, null, "липсва kind или label");
        log.ReportSuccess(Recorded("упражнение", "Leg press"));

        Assert.Single(log.Snapshot());
    }

    [Fact]
    public void Repeated_failure_for_same_operation_is_reported_once()
    {
        var log = new DiaryConfirmationLog();
        log.ReportFailure("упражнение", "Leg press", "първи отказ");
        log.ReportFailure("упражнение", "Leg press", "втори отказ");

        var snapshot = log.Snapshot();

        Assert.Single(snapshot);
        Assert.Equal("втори отказ", snapshot[0].Message);
    }

    [Fact]
    public void Void_success_clears_failure_reported_by_id()
    {
        var log = new DiaryConfirmationLog();
        log.ReportFailure(null, "id 259", "не е активен запис");
        log.ReportSuccess(new DiaryConfirmation(
            DiaryConfirmationStatus.Voided, "дневник", "Leg press", 259));

        var snapshot = log.Snapshot();

        Assert.Single(snapshot);
        Assert.Equal(DiaryConfirmationStatus.Voided, snapshot[0].Status);
    }

    [Fact]
    public void Failures_are_listed_after_completed_entries()
    {
        var log = new DiaryConfirmationLog();
        log.ReportFailure("тренировка", "Day 1 — Legs + Core", "без изричен край");
        log.ReportSuccess(Recorded("упражнение", "Leg press"));

        var snapshot = log.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(DiaryConfirmationStatus.Recorded, snapshot[0].Status);
        Assert.Equal(DiaryConfirmationStatus.Rejected, snapshot[1].Status);
    }
}
