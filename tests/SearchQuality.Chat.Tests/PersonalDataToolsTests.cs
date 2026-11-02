using SearchQuality.Chat;

namespace SearchQuality.Chat.Tests;

public sealed class PersonalDataToolsTests
{
    [Fact]
    public void Save_arguments_are_parsed_and_kind_is_normalized()
    {
        var save = PersonalDataTools.ParseSave(
            """{"kind":" Програма ","title":"3-дневна програма","content":"## Ден 1\n..."}""");

        Assert.NotNull(save);
        Assert.Equal("програма", save.Kind);
        Assert.Equal("3-дневна програма", save.Title);
        Assert.StartsWith("## Ден 1", save.Content, StringComparison.Ordinal);

        Assert.Null(PersonalDataTools.ParseSave("""{"kind":"програма"}""")); // без content
        Assert.Null(PersonalDataTools.ParseSave("не е json"));
    }

    [Fact]
    public void Replace_arguments_require_the_current_document_id()
    {
        var replace = PersonalDataTools.ParseReplace(
            """
            {
              "kind": " Програма ",
              "current_document_id": "artifact-ivan-program-42",
              "title": "Нова програма",
              "content": "# Нова програма\n\n..."
            }
            """);

        Assert.NotNull(replace);
        Assert.Equal("програма", replace.Kind);
        Assert.Equal("artifact-ivan-program-42", replace.CurrentDocumentId);
        Assert.Equal("Нова програма", replace.Title);
        Assert.Null(PersonalDataTools.ParseReplace(
            """{"kind":"програма","title":"x","content":"y"}"""));
    }

    [Fact]
    public void Edit_arguments_allow_exact_removal_and_normalize_kind()
    {
        var edit = PersonalDataTools.ParseEdit(
            """
            {
              "kind": " Програма ",
              "edits": [
                {
                  "old_text": "### Dead bug\n- 3 × 8",
                  "new_text": ""
                }
              ]
            }
            """);

        Assert.NotNull(edit);
        Assert.Equal("програма", edit.Kind);
        Assert.Null(edit.Title);
        Assert.Equal(
            [new PersonalDataTools.TextEdit("### Dead bug\n- 3 × 8", "")],
            edit.Edits);
    }

    [Fact]
    public void Exact_edits_preserve_everything_outside_the_requested_text()
    {
        const string original =
            """
            # Меню
            ## Закуска
            - Яйца: 2
            ## Вечеря
            - Ориз: 100 г
            """;
        var edits = new[]
        {
            new PersonalDataTools.TextEdit("- Яйца: 2", "- Яйца: 3"),
            new PersonalDataTools.TextEdit("## Вечеря\n- Ориз: 100 г", "")
        };

        var result = PersonalDataTools.ApplyExactEdits(original, edits);

        Assert.Null(result.Error);
        Assert.Equal(
            """
            # Меню
            ## Закуска
            - Яйца: 3

            """,
            result.Content);
    }

    [Fact]
    public void Empty_replacement_removes_the_whole_containing_row()
    {
        const string original =
            """
            | # | Exercise | Sets | Rest |
            |---|---|---|---|
            | 9 | Pallof press | 3x10 | 45s |
            | 10 | Dead bug | 3x8 | 45s |
            | 11 | Farmer's walk | 3x30m | 60s |
            """;

        var result = PersonalDataTools.ApplyExactEdits(
            original,
            [new PersonalDataTools.TextEdit("Dead bug", "")]);

        Assert.Null(result.Error);
        Assert.Equal(
            """
            | # | Exercise | Sets | Rest |
            |---|---|---|---|
            | 9 | Pallof press | 3x10 | 45s |
            | 11 | Farmer's walk | 3x30m | 60s |
            """,
            result.Content);
    }

    [Fact]
    public void Line_removal_with_trailing_newline_keeps_the_following_line()
    {
        var result = PersonalDataTools.ApplyExactEdits(
            "първи\nпремахни\nпоследен",
            [new PersonalDataTools.TextEdit("премахни\n", "")]);

        Assert.Equal(("първи\nпоследен", (string?)null), result);
    }

    [Theory]
    [InlineData("липсва")]
    [InlineData("- общ ред")]
    public void Exact_edits_reject_missing_or_ambiguous_text_without_partial_result(
        string oldText)
    {
        const string original =
            """
            # План
            - общ ред
            - уникален ред
            - общ ред
            """;

        var result = PersonalDataTools.ApplyExactEdits(
            original,
            [new PersonalDataTools.TextEdit("- уникален ред", "- променен ред"),
             new PersonalDataTools.TextEdit(oldText, "")]);

        Assert.Null(result.Content);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("""{"kind":"програма","edits":[]}""")]
    [InlineData("""{"kind":"програма","edits":[{"old_text":"x"}]}""")]
    [InlineData("""{"kind":"програма","edits":[{"old_text":"","new_text":"y"}]}""")]
    public void Invalid_edit_arguments_are_rejected(string json)
    {
        Assert.Null(PersonalDataTools.ParseEdit(json));
    }

    [Fact]
    public void Log_arguments_carry_structured_data_and_correction_id()
    {
        var log = PersonalDataTools.ParseLog(
            """{"kind":"упражнение","label":"клек","data":{"серии":3,"повторения":10,"тегло_кг":60},"replaces_id":42}""");

        Assert.NotNull(log);
        Assert.Equal("упражнение", log.Kind);
        Assert.Equal("клек", log.Label);
        Assert.Contains("\"тегло_кг\":60", log.DataJson, StringComparison.Ordinal);
        Assert.Equal(42, log.ReplacesId);

        var skipped = PersonalDataTools.ParseLog(
            """{"kind":"упражнение","label":"коремни","data":{"статус":"пропуснато"}}""");
        Assert.NotNull(skipped);
        Assert.Null(skipped.ReplacesId);
        Assert.False(skipped.AllowDuplicate);
        Assert.Contains("пропуснато", skipped.DataJson, StringComparison.Ordinal);

        var second = PersonalDataTools.ParseLog(
            """{"kind":"хранене","label":"банан","allow_duplicate":true}""");
        Assert.NotNull(second);
        Assert.True(second.AllowDuplicate);

        Assert.Null(PersonalDataTools.ParseLog("""{"kind":"упражнение"}""")); // без label
    }

    [Fact]
    public void Bulk_log_arguments_parse_one_to_twenty_entries()
    {
        var logs = PersonalDataTools.ParseLogs(
            """
            {
              "entries": [
                {
                  "kind": "упражнение",
                  "label": "Chest press",
                  "data": {"серии": 4, "тегло_кг": 43}
                },
                {
                  "kind": "готвене",
                  "label": "Къри с ориз",
                  "data": {"порции": 3}
                }
              ]
            }
            """);

        Assert.NotNull(logs);
        Assert.Equal(2, logs.Count);
        Assert.Equal("упражнение", logs[0].Kind);
        Assert.Equal("Chest press", logs[0].Label);
        Assert.Equal("готвене", logs[1].Kind);
        Assert.Contains("\"порции\": 3", logs[1].DataJson, StringComparison.Ordinal);

        Assert.Null(PersonalDataTools.ParseLogs("""{"entries":[]}"""));
        Assert.Null(PersonalDataTools.ParseLogs(
            """{"entries":[{"kind":"упражнение"}]}"""));

        var tooMany = System.Text.Json.JsonSerializer.Serialize(new
        {
            entries = Enumerable.Range(0, 21).Select(index => new
            {
                kind = "entry",
                label = $"item-{index}"
            })
        });
        Assert.Null(PersonalDataTools.ParseLogs(tooMany));
    }

    [Fact]
    public void Log_past_day_date_is_parsed_and_garbage_is_ignored()
    {
        var past = PersonalDataTools.ParseLog(
            """{"kind":"тренировка","label":"Day 1 — Legs + Core","happened_on":"2026-07-21"}""");
        Assert.NotNull(past);
        Assert.Equal(new DateOnly(2026, 7, 21), past.HappenedOn);
        Assert.True(past.HappenedOnProvided);

        // Суровата стойност остава: извикващият връща грешка на модела,
        // вместо датата тихо да стане „днес".
        var garbage = PersonalDataTools.ParseLog(
            """{"kind":"тренировка","label":"Day 1","happened_on":"вчера"}""");
        Assert.NotNull(garbage);
        Assert.Null(garbage.HappenedOn);
        Assert.Equal("вчера", garbage.HappenedOnRaw);
        Assert.True(garbage.HappenedOnProvided);

        var number = PersonalDataTools.ParseLog(
            """{"kind":"тренировка","label":"Day 1","happened_on":20260721}""");
        Assert.NotNull(number);
        Assert.Null(number.HappenedOn);
        Assert.Equal("20260721", number.HappenedOnRaw);
        Assert.True(number.HappenedOnProvided);

        var empty = PersonalDataTools.ParseLog(
            """{"kind":"тренировка","label":"Day 1","happened_on":""}""");
        Assert.NotNull(empty);
        Assert.Null(empty.HappenedOn);
        Assert.Equal("", empty.HappenedOnRaw);
        Assert.True(empty.HappenedOnProvided);

        var omitted = PersonalDataTools.ParseLog(
            """{"kind":"тренировка","label":"Day 1"}""");
        Assert.NotNull(omitted);
        Assert.Null(omitted.HappenedOn);
        Assert.Null(omitted.HappenedOnRaw);
        Assert.False(omitted.HappenedOnProvided);
    }

    [Fact]
    public void Log_query_defaults_are_applied_and_days_are_clamped()
    {
        Assert.Equal(
            new PersonalDataTools.LogQuery(null, 14, null, null, false),
            PersonalDataTools.ParseLogQuery("{}", defaultDays: 14));
        Assert.Equal(
            new PersonalDataTools.LogQuery("хранене", 7, null, null, false),
            PersonalDataTools.ParseLogQuery(
                """{"kind":"хранене","days":7}""",
                defaultDays: 14));
        Assert.Equal(
            new PersonalDataTools.LogQuery(
                null,
                365,
                new DateOnly(2026, 7, 31),
                "2026-07-31",
                true),
            PersonalDataTools.ParseLogQuery(
                """{"days":9999,"happened_on":"2026-07-31"}""",
                defaultDays: 14));
    }

    [Fact]
    public void Void_arguments_require_positive_id_and_reason()
    {
        Assert.Equal(
            new PersonalDataTools.VoidArgs(259, "не е посочено упражнение"),
            PersonalDataTools.ParseVoid(
                """{"id":259,"reason":"не е посочено упражнение"}"""));
        Assert.Null(PersonalDataTools.ParseVoid("""{"id":0,"reason":"грешка"}"""));
        Assert.Null(PersonalDataTools.ParseVoid("""{"id":259}"""));
    }

    [Fact]
    public void Progress_tool_schema_exposes_exact_day_without_requiring_it()
    {
        var tool = Assert.Single(
            PersonalDataTools.Shared,
            item => item.Name == "get_progress");

        Assert.Contains("\"happened_on\"", tool.ParametersJsonSchema, StringComparison.Ordinal);
        Assert.Contains("\"required\":[\"kind\"]", tool.ParametersJsonSchema, StringComparison.Ordinal);
    }
}
