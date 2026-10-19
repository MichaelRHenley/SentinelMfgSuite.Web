# Sentinel Industrial Data & AI Readiness Scoring Contract (Proposed v1)

**Status:** Authoritative proposed scoring specification; not yet implemented.  
**Scope:** Deterministic, version-aware scoring of submitted questionnaire responses.  
**Normative terms:** “MUST”, “MUST NOT”, and “SHOULD” express requirements of this proposed contract.

## 1. Contract principles

1. Scoring MUST use immutable question IDs and the actual stored answer values. Customer-facing display numbers MUST NOT be used for lookup, mappings, or calculations.
2. The assessment's explicit questionnaire version and response `schemaVersion` MUST match. Unsupported versions and mismatches MUST yield a no-score result with an explicit reason; implementations MUST NOT silently fall back to another version.
3. Scoring MUST be deterministic and separate from questionnaire validation, submission, persistence, and answer mutation. It MUST NOT change a response or cause submission to succeed or fail.
4. Calculated results are transient in this initial implementation. They MUST NOT be persisted, added to migrations, or written into `AssessmentResponse`.
5. A choice not listed as scoreable below MUST NOT be guessed, coerced, or treated as zero. It is reported as unmapped/unsupported (or insufficient where specifically identified below).
6. Missing, explicit unknown, and `reliability_not_established` values are insufficient evidence, never zero. There is no implicit N/A support.
7. All results are self-reported unless separately verified by a recorded, independent verification process. Evidence coverage is not evidence confidence.

## 2. Version behavior and scored item membership

| Questionnaire version | Contract behavior | Overall result |
|---|---|---|
| V1 (`1.0`) | `HistoricalNoScore`; retain for historical compatibility only. No score mappings or assumptions are applied. | Withheld; reason `HistoricalVersionNotScored`. |
| V2 (`2.0`) | Evaluate only the listed eligible dimension items and apply V2 evidence gates. D1 is always withheld. | Always withheld; reason `OverallNotAvailableForV2`. |
| V3 (`3.0`) | Evaluate the listed items and V3 evidence gates. | Eligible only when D1, D2, and D3 all qualify. |

Only these items belong to the proposed score denominator:

| Dimension | Weight | V2 eligible item IDs | V3 eligible item IDs |
|---|---:|---|---|
| D1 — Connectivity & Integration | 30% | None; always withheld | `q15.integration_level`, `q33`, `q34` (3 items) |
| D2 — Data Quality & Contextualization | 45% | `q17.history_choice`, `q19`, `q20`, `q21` (4 items) | Those four plus `q35`, `q36` (6 items) |
| D3 — Analytics & Reporting | 25% | `q24`, `q31`, `q32` (3 items) | Same three items |

V1 has historical definitions containing some of the same IDs/fields, but its policy is no scoring. Other V1/V2/V3 questions—including `q13` and `q14`—remain outside these mappings and are qualitative/contextual only. In particular, V2 q31 and q32 are optional questionnaire items; their optionality does not remove them from the score-coverage denominator when that V2 dimension is evaluated.

## 3. Exact proposed item mappings

Question and field identifiers below are schema identifiers, not customer display labels. For `q15`, the stored composite field key is `integration_level`; for `q17`, it is `history_choice`. `q17.retention` MUST NOT be scored.

### D1 — Connectivity & Integration (30%)

| Immutable item key | Stored choice value | Score |
|---|---|---:|
| `q15.integration_level` | `manual_transfers` | 0 |
|  | `isolated` | 1 |
|  | `partially` | 2 |
|  | `mostly` | 3 |
|  | `unknown` | Insufficient; unscored |
| `q33` | `none` | 0 |
|  | `less_than_quarter` | 1 |
|  | `about_quarter_to_half` | 2 |
|  | `most` | 3 |
|  | `nearly_all` | 4 |
|  | `unknown` | Insufficient; unscored |
| `q34` | `not_available` | 0 |
|  | `often_late_or_incomplete` | 1 |
|  | `sometimes_late_or_incomplete` | 2 |
|  | `usually_on_time` | 3 |
|  | `consistently_reliable` | 4 |
|  | `unknown` | Insufficient; unscored |

`q13` and `q14` remain qualitative only and do not contribute to D1 maturity or its score denominator.

### D2 — Data Quality & Contextualization (45%)

| Immutable item key | Stored choice value | Score |
|---|---|---:|
| `q17.history_choice` | `no_historical` | 0 |
|  | `real_time_only` | 1 |
|  | `some_sources` | 2 |
|  | `most_relevant` | 3 |
|  | `unknown` | Insufficient; unscored |
| `q19` | `no` | 0 |
|  | `partial` | 2 |
|  | `yes` | 4 |
|  | `unknown` | Insufficient; unscored |
| `q20` | `none` | 0 |
|  | `inconsistent` | 1 |
|  | `partial` | 2 |
|  | `standardized` | 4 |
|  | `unknown` | Insufficient; unscored |
| `q21` | `no` | 0 |
|  | `manual_match` | 1 |
|  | `partial` | 2 |
|  | `automatic` | 4 |
|  | `unknown` | Insufficient; unscored |
| `q35` | `no_checks` | 0 |
|  | `informal_checks` | 1 |
|  | `some_checks` | 2 |
|  | `routine_checks` | 3 |
|  | `systematic_monitoring` | 4 |
|  | `unknown` | Insufficient; unscored |
| `q36` | `frequently_missing` | 0 |
|  | `often_missing` | 1 |
|  | `sometimes_missing` | 2 |
|  | `rarely_missing` | 3 |
|  | `almost_never_missing` | 4 |
|  | `unknown` | Insufficient; unscored |

Do not score `q17.retention`, any other `q17` composite field, or qualitative `q22` issue selections/notes.

### D3 — Analytics & Reporting (25%)

| Immutable item key | Stored choice value | Score |
|---|---|---:|
| `q24` | `not_calculated` | 0 |
|  | `manual` | 1 |
|  | `mix` | 2 |
|  | `automated_partial` | 3 |
|  | `automated_validated` | 4 |
|  | `unknown` | Insufficient; unscored |
| `q31` | `frequent_discrepancies` | 1 |
|  | `generally_accurate` | 3 |
|  | `consistently_accurate` | 4 |
|  | `reliability_not_established` | Insufficient; unscored |
|  | `unknown` | Insufficient; unscored |
| `q32` | `not_used` | 0 |
|  | `limited_ad_hoc` | 1 |
|  | `retrospective_reporting` | 2 |
|  | `investigate_improve` | 3 |
|  | `proactive_decisions` | 4 |
|  | `unknown` | Insufficient; unscored |

## 4. Answer interpretation and evidence coverage

- **Scoreable:** The item is present and its actual stored value exactly matches a scored value in the relevant version/dimension mapping. A score of `0` is a valid scoreable maturity value; it MUST NOT be confused with absence or insufficient evidence.
- **Insufficient:** The mapped item is absent, blank, explicitly `unknown`, or (for q31) `reliability_not_established`. It contributes no maturity points, remains in the coverage denominator, and receives a reason such as `Missing`, `Unknown`, or `ReliabilityNotEstablished`.
- **Unmapped:** A value outside the mapping is reported with its item ID and raw value as `UnmappedValue`; it MUST NOT be assigned a nearest score. It contributes no maturity points and does not count as scoreable. The item remains in the denominator because it is an eligible item. This includes unsupported values not named in the table.
- **No implicit N/A:** No value or absent field is interpreted as “not applicable.” An absent/missing value is insufficient evidence. A separate explicit N/A rule would require a later version of this specification.
- For a composite item, only the named field is considered. The question's other fields are not substituted for it.
- For a single-select mapped item, a missing `selected` value is insufficient. If multiple/invalid answer shapes reach scoring despite submission validation, report an invalid/unmapped item; do not guess.

For an evaluated dimension:

`EvidenceCoverage = scoreable mapped items / total mapped eligible items`

Coverage is shown as both a count (for example, `4/6`) and optionally a percentage. It is independent of the arithmetic maturity score: insufficient items do not contribute zero to maturity, but remain in the coverage denominator. The denominator for each dimension is fixed by its version membership table, even when answers are missing or unknown. Coverage indicates availability of mapped answers only; it does not measure reliability, verification, or confidence.

## 5. Evidence gates and withholding

A dimension is eligible only when all of its version-specific minimum and inclusion requirements are met. A dimension below its gate has no maturity score or classification, but still reports its item statuses, coverage, and every gate failure reason.

| Version | Dimension | Qualification requirement |
|---|---|---|
| V2 | D1 | Always withheld (`NotScoredForV2`). |
| V2 | D2 | At least 3 of 4 scoreable items. |
| V2 | D3 | At least 2 of 3 scoreable items. |
| V2 | Overall | Always withheld (`OverallNotAvailableForV2`), even if D2 and D3 qualify. |
| V3 | D1 | At least 2 of 3 scoreable items, including `q33` or `q34`. |
| V3 | D2 | At least 4 of 6 scoreable items, including `q35` or `q36`. |
| V3 | D3 | At least 2 of 3 scoreable items. |
| V3 | Overall | All three dimensions must qualify. |
| V1 | All dimensions | HistoricalNoScore; no scoring. |

Recommended explicit withholding reason codes include `HistoricalVersionNotScored`, `UnsupportedQuestionnaireVersion`, `SchemaVersionMismatch`, `NotScoredForV2`, `InsufficientScoreableItems`, `RequiredEvidenceGroupMissing`, and `OverallNotAvailableForV2` / `OverallRequiresAllDimensions`. A result may include multiple dimension reasons. Withheld results MUST NOT show a numeric zero or maturity class as a substitute for a missing score.

## 6. Calculations and numeric policy

### Dimension score

For a qualified dimension, take the arithmetic mean of its scoreable 0–4 item values and multiply by 25:

`DimensionScoreRaw = (sum of scoreable item values / scoreable item count) × 25`

Only scoreable items are in this mean. Unknown, missing, and unmapped items do not enter the numerator or maturity denominator. The evidence gate must pass before a dimension score is eligible.

### Overall score

For V3 only, and only when D1, D2, and D3 all qualify:

`OverallScoreRaw = D1ScoreRaw × 0.30 + D2ScoreRaw × 0.45 + D3ScoreRaw × 0.25`

Dimension weights total 100%. Do not renormalize weights when any dimension is withheld; withhold overall instead. V2 overall is always withheld.

### Rounding and classification

- Retain full decimal precision (or an exact rational representation) for item aggregation, dimension scores, and overall score until display/classification.
- For display, round to the nearest integer using midpoint-away-from-zero (half-up for the nonnegative 0–100 range). Do not round intermediate item means or dimension values before calculating overall.
- Classify only eligible results, using the displayed integer score: `0–24 Foundational`, `25–49 Developing`, `50–74 Established`, `75–100 Advanced`. Bounds are inclusive integer bounds. Thus 24.5 displays as 25 and is Developing; 74.5 displays as 75 and is Advanced. This avoids gaps caused by fractional values.
- A withheld result has no displayed numeric score and no classification.

### Calculation example

For V3 D1 values `q15.integration_level=mostly (3)`, `q33=nearly_all (4)`, and `q34=unknown` (insufficient), coverage is `2/3`, and the evidence group condition passes because q33 is scoreable. D1 raw score is `((3+4)/2)×25 = 87.5`; displayed score is `88`, classified Advanced. The unknown q34 is not treated as zero.

If qualified V3 dimensions have raw scores D1=87.5, D2=62.5, and D3=50, overall raw is `87.5×.30 + 62.5×.45 + 50×.25 = 66.875`; display 67 and classify Established. If any dimension fails its evidence gate, the overall is withheld rather than calculated with adjusted weights.

## 7. Advisory consultant-review flags

Flags are deterministic review prompts, not errors, validation rules, scoring overrides, or automatic score suppression. They MUST preserve the underlying answers and normal score eligibility. Each flag should return a stable code, severity (initially informational), relevant immutable item IDs/raw values, and an explanatory message.

1. **`ConnectivityReliabilityConflict`:** `q33=none` while `q34` indicates reliable automatic exchange, specifically `usually_on_time` or `consistently_reliable`. q33 concerns the share of needed data connected; q34 concerns timeliness for automatic connections. Apparent conflict may have a legitimate explanation (for example, few connections exist but those limited connections are reliable).
2. **`DataCompletenessReview`:** `q35=systematic_monitoring` while `q36` indicates frequent missing records (`frequently_missing` or `often_missing`). Systematic detection can coexist with frequent gaps; a consultant should clarify detection versus remediation.
3. **`ReportingReliabilityConflict`:** `q24=not_calculated` while `q31=consistently_accurate`. The terms may refer to different reports, KPI sets, facilities, or time periods; clarify rather than suppressing the score.

Flags MUST NOT automatically suppress scoring or infer that either answer is false. Other contradiction rules require explicit specification and tests before implementation.

## 8. Provenance and limitations

- Label every result `SelfReported` by default. Current questionnaire answers are respondent-provided; score calculation does not independently verify them.
- A later independent verification workflow may annotate evidence provenance, verifier, and verification date separately. It must not silently rewrite the self-reported answer or alter the score without an explicitly approved rule.
- Evidence coverage is not evidence confidence and is not a substitute for verification.
- The score summarizes mapped answers and does not establish causal performance, operational safety, ROI, AI suitability, or audit assurance.
- Mappings are proposed policy choices, not empirically validated measurements. Treat results as decision-support for consultant review, not as certification or an automated recommendation.
- Current definitions contain qualitative/context fields and other choices outside the mappings. They must remain visible to reviewers but cannot be scored by inference.

## 9. Expected unit-test contract

Tests should be table-driven and require no database, network, or external AI service.

1. **Version handling:** V1 returns HistoricalNoScore; V2 and V3 resolve explicitly; unsupported version and assessment/response schema mismatch withhold all scores.
2. **Mapping exhaustiveness:** Every scored value maps to the exact specified 0–4 value; each explicit `unknown` and q31 `reliability_not_established` is insufficient; arbitrary values are unmapped and never guessed.
3. **Stable identifiers:** mapping is keyed by question IDs and composite field IDs (`q15.integration_level`, `q17.history_choice`), never display labels/numbers. `q17.retention`, q13, and q14 do not contribute.
4. **Coverage:** missing, unknown, reliability-not-established, and unmapped answers remain in the eligible-item denominator and do not contribute to maturity; scoreable zero does count as scoreable.
5. **Evidence gates:** V2 D1 always withheld; V2 D2 at 2/4 withheld and 3/4 eligible; V2 D3 at 1/3 withheld and 2/3 eligible; V2 overall always withheld. V3 D1/D2 minimum counts and required q33/q34 or q35/q36 inclusion are independently tested; V3 D3 1/3 versus 2/3; overall requires all dimensions.
6. **Math:** dimension mean ×25; weighted overall uses raw unrounded dimension scores; do not renormalize weights; display midpoint rounding and class boundaries (24.49/24.5, 49.5, 74.49/74.5) are tested.
7. **Withholding:** every no-score path returns explicit reason(s), coverage/item detail where applicable, and no zero score or classification.
8. **Contradictions:** each specified q33/q34, q35/q36, and q24/q31 combination yields its advisory flag; near misses do not; flags do not change score eligibility or score values.
9. **Provenance:** output defaults to SelfReported; coverage is represented independently from provenance/confidence.
10. **Purity:** service has no database or persistence dependency and does not mutate the input response.

## 10. Proposed implementation sequence

1. Review and approve this contract, including mappings, gates, rounding, and contradiction rules.
2. Implement immutable result models and explicit version-specific mapping tables in a standalone scoring namespace/service.
3. Implement pure evaluation, coverage, withholding reasons, classifications, and advisory flags.
4. Add the unit-test matrix above and run tests without PostgreSQL integration dependencies.
5. In a separately approved change, inject the scorer into the internal Review Responses read-only workflow and display transient results and reasons. Do not change submission, persistence, or questionnaire validation, and do not persist scores.

## 11. Definition verification and mismatches

The proposed mapped IDs, nested fields, and choice values were checked against the current definitions in `QuestionnaireV1.cs`, `QuestionnaireV2.cs`, and `QuestionnaireV3.cs`:

- `q15` is a composite question with nested choice field `integration_level`; all five requested values exist.
- `q17` is a composite question with nested choice field `history_choice`; all five requested values exist. `retention` is a separate field and is not scored.
- `q19`, `q20`, `q21`, and `q24` contain every requested value, including `unknown`.
- V2 q31 contains `frequent_discrepancies`, `generally_accurate`, `consistently_accurate`, `reliability_not_established`, and `unknown`; V2 q32 contains every requested value.
- V3 q33, q34, q35, and q36 contain every requested mapped value and `unknown`.
- V2 inherits the original q15/q17/q19/q20/q21/q24 definitions and adds q31/q32. V3 inherits V2 and adds q33–q36.

**No mismatch was found between the requested mappings and current choice/field definitions.** V1 also has several corresponding base questions/fields, but remains explicitly unscored under this contract. Questionnaire definitions and response data are unchanged by this document.
