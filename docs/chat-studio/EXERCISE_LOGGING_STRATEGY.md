# Structured Exercise Logging Strategy

[Chat Studio](README.md) · [Architecture](ARCHITECTURE.md) · [Operations](OPERATIONS.md)

## Document Status

This is an exploratory design document. It defines the problem, candidate solutions, and the methodology for comparing them. It does not establish a final contract and does not imply that any candidate strategy has been approved for implementation or production rollout.

Before making code or prompt changes, a strategy must be selected using representative conversations, automated checks, and backward-compatibility evaluation.

## Problem

When a user confirms workout completion with a short reply such as *"done"* or *"I completed all exercises"*, the language model may record only a generic status even though the immediate conversation or the active training program already contains sets, reps, and load. Such an entry is readable by a human but is a weak source for statistics and progress tracking because numeric parameters are missing.

The opposite risk also exists: an overly rigid rule can push the model to invent exact reps or weights when the program specifies a range, omits external load, or uses bodyweight, time, or distance.

Therefore, the goal is not simply to make every numeric field mandatory. We must define when context is sufficiently reliable, how different load modalities are represented, and when an entry must be rejected or clarified.

## Goals

- A completed exercise should not be reduced to a bare status when structured parameters are available in reliable current context.
- Values from an immediately displayed or freshly read training program must not be treated as fabricated.
- Distinguish between planned parameters, explicitly reported actuals, and values derived from a general completion confirmation.
- The structure must support accurate SQL progress queries.
- Avoid unnecessary clarifying questions when the answer is already present in the current conversation or active deliverable.
- Never fabricate missing numbers.
- Batch logging of a full workout must remain atomic.
- Corrections, skipped exercises, and genuine second executions on the same day must preserve existing audit guarantees.

## Hierarchy of Value Sources to Compare

A candidate strategy must explicitly rank value sources by reliability:

1. Values explicitly stated by the user in the current turn;
2. User corrections relative to the workout just displayed;
3. Parameters from the workout displayed immediately before the general confirmation;
4. The applicable section of the active deliverable just read via `get_deliverable`;
5. Older conversation history or archived memory.

The first two sources are directly reported. The third and fourth are contextually confirmed, though we must decide whether they are stored as actual values, as execution-as-planned, or with a separate provenance marker. Archived memory must never be used on its own to populate numeric fields.

## Open Questions Without a Final Decision

### Actual Execution vs. Prescription

A reply like *"I did everything"* confirms the exercises were performed, but does not always prove the exact count in every set. One of the following semantics must be chosen:

- All parameters from the displayed plan are accepted as actually executed;
- Parameters are saved as execution-by-plan with a dedicated provenance field;
- Only values provable as exact actuals are stored in numeric fields, while the rest remain described as prescription.

### Ranges

A value such as `3 × 12–15` must not silently become `12` or `15`. Candidate representations include:

- Separate numeric fields (`reps_min` and `reps_max` / `повторения_мин` and `повторения_макс`);
- A single canonical numeric representative for statistics plus the full range in the note;
- Separating the planned range from actually reported repetitions.

The choice must account for how SQL progress queries, the Web UI, and Telegram render these fields.

### Load Without Numeric External Weight

Not every exercise has a meaningful `weight_kg` (`тегло_кг`):

- Bodyweight exercises;
- Resistance band or machine exercises without a reported stack weight;
- Time under tension;
- Loaded carries over distance;
- Isometric holds.

`тегло_кг: 0` must not be recorded automatically, because zero external weight and unknown weight are not equivalent. A contract with fields such as `натоварване` (load description), `време_сек` (duration in seconds), and `дистанция_м` (distance in meters) must be evaluated without breaking numeric aggregation.

### Missing Parameters

We must determine whether an exercise with genuinely missing parameters:

- Is rejected until a brief clarification is provided;
- Is recorded as an incomplete but explicitly flagged entry;
- Is recorded only when the omission is natural for that exercise modality.

Before asking the user a question, the model must check the immediate conversation and the active training program.

## Candidate Strategies

### A. Prompt-Only Change

A general rule is added to the runtime instructions of both the orchestrator and the specialist. It states that the immediate conversation and the freshly read training program are valid parameter sources, and that a bare status must not replace available parameters.

The advantage is fast iteration without schema changes. The drawback is the lack of a deterministic guarantee—the model can still submit an incomplete JSON object.

### B. Prompt + More Precise Tool Contract

In addition to the prompt rule, the descriptions and JSON schemas of `log_activity` and `log_activities` are refined. Canonical fields for exercises, rep ranges, bodyweight, duration, and distance are explicitly documented.

This gives the model clearer structure, though the server can still accept an incomplete or semantically inconsistent entry.

### C. Context Rule, Tool Contract, and Server-Side Validation

Strategy B is augmented with pre-commit server validation. A completed exercise lacking minimum structured information is rejected with a machine-readable reason, enabling the model to either read the program or ask only for the missing fact.

This is the strongest candidate for reliability, but requires a careful conditional contract so valid bodyweight, timed, or distance exercises are not falsely rejected.

### D. Dedicated Workout Execution Tool

Instead of the general diary tool, a specialized tool for exercises or completed workout sessions is introduced with a stricter schema, program provenance, and separate planned vs. actual parameters.

This allows the cleanest domain contract, but increases complexity, migration cost, and the risk of two competing tools for logging the same event.

## Preliminary Hypothesis

Strategy C appears best balanced if minimum validity is conditional on exercise modality and if value provenance is represented clearly. This is a hypothesis for testing, not a final decision.

The shared rule must reach both the primary orchestrator (`chat`) and the specialist (`coach`), because both can invoke diary tools; editing only the fitness prompt is insufficient.

## Evaluation Matrix

Every candidate strategy must be evaluated against at least the following criteria:

| Criterion | What Is Verified |
|---|---|
| Completeness | Available sets, reps, and load are captured in the structured entry |
| Factual Accuracy | No numbers appear that do not follow from the user or valid current context |
| Context Resilience | *"I did everything"* uses the exact workout shown immediately before |
| Clarification Discipline | No question is asked when the active program already contains the answer |
| Exercise Variety | Barbell/machine weights, bodyweight, ranges, time, and distance all work |
| Atomicity | A full workout batch is either recorded completely or not recorded at all |
| Auditability | Corrections preserve the old row and reference the proper `replaces_id` |
| Compatibility | Existing progress queries and Web/Telegram UIs continue to work |
| Explainability | The stored data makes clear what was explicitly reported vs. derived from the plan |

## Required Test Scenarios

1. The user reports exact sets, reps, and weight in a single message.
2. The assistant displays a workout, after which the user replies *"I did all exercises"*.
3. The program is not in the immediate message text and must be read via `get_deliverable`.
4. The user follows the program but explicitly changes the weight or reps for one exercise.
5. The program contains a rep range such as `3 × 12–15`.
6. The exercise is a bodyweight movement.
7. The exercise is measured by time or distance.
8. The program does not specify a weight and the user does not state one either.
9. One exercise is explicitly skipped.
10. Part of the exercises are already logged, and the general confirmation arrives afterward.
11. The user corrects an already logged value.
12. The user genuinely performs the same exercise a second time on the same day.
13. Archive memory contains an older program that differs from the current active deliverable.
14. A batch contains one invalid entry and must be rejected atomically.

## Proposed Experiment Process

1. Fix the expected structured output for all 14 test scenarios before editing prompts or code.
2. Build a small regression suite of anonymized conversations and synthetic edge cases.
3. Run candidate strategies multiple times with the same model and settings, since a single successful conversation does not prove stability.
4. Evaluate results automatically for schema compliance, completeness, and atomicity, and manually for factual accuracy and absence of redundant questions.
5. Verify SQL progress queries, Web UI rendering, and Telegram formatting for every new field type.
6. Select a winning strategy and only then finalize migration, rollout, and rollback steps.

## Scope Boundary of This Document

This document intentionally omits a final JSON schema, code change, production prompt, or database migration for existing rows. Those must be the outcome of the experiment rather than an upfront assumption.
