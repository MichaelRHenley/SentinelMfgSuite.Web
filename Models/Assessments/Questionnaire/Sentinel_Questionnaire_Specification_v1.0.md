# Sentinel Industrial Data & AI Readiness Assessment — Canonical Questionnaire v1.0

**Status:** Approved business design; implementation reference. **Scope:** one manufacturing site, one priority operational challenge, up to five data sources. **Audience:** manufacturing management and technical staff. **Do not treat example responses as customer data.**

## Implementation rules
- Preserve exactly six sections and stable IDs `q01`–`q30` in the order below. Main questions and their subordinate fields do not create extra numbered questions.
- Supported input types: text, long text, single select, multi select, repeatable source record. Use stable machine-readable choice values; labels below are authoritative, and values may be assigned consistently by implementer.
- Explicit `Unknown / Not sure` is distinct from an unanswered/null value. When Unknown, None, or Not collected is logically exclusive, enforce mutual exclusivity against substantive options. Do not force customers to guess.
- The system flags unknown, incomplete and uncertain responses for the **15-minute discovery call**. Flags are determined from answer content, not indiscriminately from question identity. Customer self-reports are unverified.
- Server-side validation is authoritative. Drafts may be incomplete; final submission validates required fields and selection/record limits. No credentials, passwords, network addresses, or secret access details requested.
- Do not change existing assessment entities, EF mappings, or database schema merely to implement the definition. The existing `AssessmentResponse.Data` JSONB column will hold a versioned answer payload in a later phase.
- Conditional supplemental fields should appear when applicable, without preventing saving an incomplete draft. Any extra descriptions should remain associated with the relevant numbered question.
- The `q06` selected challenges constrain `q07` to one of the selected options. `q12` should allow identifying relevant systems from `q11`, or explicitly Unknown, and remain accessible even if `q11` is Unknown.

## Section 1 — Facility & Operations (q01–q05)

**q01 — Facility name and location.** Text fields: `facility_name` (required on submission) and `facility_location` (required on submission). Do not require a precise street address.

**q02 — What products or product families are manufactured at this facility?** Optional text / long text.

**q03 — Which manufacturing processes operate at this facility?** Multi-select: Discrete manufacturing / assembly; Continuous process manufacturing; Batch manufacturing; Packaging / converting; Material handling / warehousing; Other (specify); Unknown / Not sure.

**q04 — Approximately how many production lines or work centres are at this facility?** Single-select: 1–5; 6–10; 11–25; More than 25; Unknown / Not sure.

**q05 — What is the facility's typical operating schedule?** Single-select: One shift; Two shifts; Three shifts; 24 hours / 7 days; Variable / seasonal; Unknown / Not sure.

## Section 2 — Business Challenges (q06–q10)

**q06 — What are the facility's most significant operational challenges? Select up to three.** Multi-select, maximum 3, minimum 1 on submission: Unplanned downtime; Equipment reliability / maintenance; Throughput / bottlenecks; Quality / scrap / rework; Scheduling / changeovers; Manual data collection / reporting; Production visibility / inaccurate reporting; Energy consumption / costs; Traceability / compliance; Other (specify).

**q07 — Which one of the selected challenges is the highest priority for this assessment?** Single-select sourced from the q06 selections; required on submission. This defines the assessment's primary scope; other selected challenges remain context.

**q08 — How is this priority challenge currently identified, investigated, and reported?** Long text. Encourage description of operator reports, systems, events, and decision process.

**q09 — What is the current impact of this challenge?** Single-select: Measured / quantified; Estimated; Significant but not quantified; Unknown / Not sure. Optional `impact_details` text (metrics, frequency, costs, downtime, scrap, etc.; no currency requirement).

**q10 — What information would help your team make better decisions about this challenge?** Long text.

## Section 3 — Systems & Data Sources (q11–q16)

**q11 — Which systems or technologies are currently used at this facility? Select all that apply.** Multi-select: PLCs / industrial controllers; SCADA / HMI; Process historian; Manufacturing Execution System (MES); Enterprise Resource Planning (ERP); Computerized Maintenance Management System (CMMS); Quality management systems; SQL / other databases; Spreadsheets / manual records; Cloud / IoT platforms; Other (specify); Unknown / Not sure.

**q12 — Which of these systems are relevant to investigating the priority operational challenge?** Multi-select of applicable q11 systems plus Unknown / Not sure; allow Unknown even if q11 is Unknown. Do not make this an MES vendor question.

**q13 — List up to five data sources potentially relevant to the priority challenge.** Repeatable records, 0–5 allowed for drafts and up to five for submission; partially known sources are acceptable. Each record has:
- `source_name`: name or description (required for a nonempty record)
- `system_type`: category (PLC, SCADA, Historian, MES, ERP, CMMS, Quality, Database, Cloud/IoT, Spreadsheet/manual, Other, Unknown)
- `vendor_platform`: optional free text, may be blank or Unknown
- `available_data`: optional free text describing signals, events, orders, production, etc.
- `connection_method`: single-select Unknown / Not sure; OPC UA; MQTT; SQL / database; API; File / export; Vendor-specific integration; No known connection
- `access_status`: single-select Unknown / Not sure; Available; Requires internal approval; Requires vendor involvement; Unavailable
Do not ask for passwords, IP addresses, hostnames, or access credentials. Vendor/platform, method and status must **not** be required to save or submit.

**q14 — How is operational data currently collected or transferred? Select all that apply.** Multi-select: Automatically from equipment / controls; SCADA / historian collection; APIs; Database queries / exports; Manual operator entry; Paper forms / spreadsheets; Not currently collected; Unknown / Not sure.

**q15 — How integrated are these systems today?** Single-select: Mostly integrated; Partially integrated; Largely isolated; Primarily manual transfers; Unknown / Not sure. Optional `integration_notes` text.

**q16 — Who is responsible for maintaining these systems and approving data access?** Text / long text identifying roles or departments, not necessarily individual names.

## Section 4 — Data Quality & Context (q17–q22)

**q17 — Is historical operational data available?** Single-select: Most relevant sources; Some sources; Real-time data only; No historical data; Unknown / Not sure. Optional `retention` single-select: Under 30 days; 1–6 months; 6–12 months; Over 12 months; Varies by source; Unknown / Not sure.

**q18 — How frequently is operational data recorded or updated?** Single-select: Sub-second; Every few seconds; Every few minutes; Hourly or less frequently; Event-based; Varies by source; Manual only; Unknown / Not sure. Optional `frequency_notes`.

**q19 — Are timestamps synchronized across systems?** Single-select: Yes; Partially; No; Unknown / Not sure.

**q20 — Are equipment, production-line and variable identifiers consistent and documented?** Single-select: Standardized and documented; Partially standardized; Inconsistent; No established convention; Unknown / Not sure.

**q21 — Can equipment data be associated with production orders, products, batches or operating conditions?** Single-select: Automatically; Partially; Through manual matching; No; Unknown / Not sure.

**q22 — What data-quality problems are known or suspected? Select all that apply.** Multi-select: Missing data / gaps; Incorrect values; Unreliable / disconnected sensors; Inconsistent naming; Incorrect / inconsistent timestamps; Manual-entry errors; Inaccurate downtime / reason codes; Duplicate records; Data inaccessible; Other (specify); No known issues; Unknown / Not sure. Optional `quality_notes`.

## Section 5 — Analytics & AI Readiness (q23–q26)

**q23 — What tools are currently used for reporting or analytics? Select all that apply.** Multi-select: Excel / spreadsheets; Power BI; Tableau / Qlik / other BI tools; SCADA / HMI dashboards; Historian reporting; MES / ERP reporting; Custom applications; Cloud analytics; Other (specify); None; Unknown / Not sure.

**q24 — How are operational KPIs calculated and validated?** Single-select: Automated and validated; Automated but only partly validated; Mix of automated and manual; Primarily manual; Not currently calculated; Unknown / Not sure. Optional `kpi_notes`.

**q25 — Has the facility used or evaluated AI / machine learning for manufacturing?** Single-select: In production; Pilot / proof of concept; Evaluated but not deployed; Planning / considering; No; Unknown / Not sure. Optional `ai_experience_notes`.

**q26 — Which internal or external capabilities are available to support a data or analytics initiative? Select all that apply.** Multi-select: Controls / automation; IT / networking; OT cybersecurity; Software / systems integration; Data engineering; BI / reporting; Machine learning / data science; Manufacturing / process subject-matter expertise; External integrators / vendors; Unknown / Not sure.

## Section 6 — Objectives & Constraints (q27–q30)

**q27 — What measurable operational outcome would make this assessment valuable?** Long text `desired_outcome`. Supplemental `baseline_available` single-select: Yes; Partially; No; Unknown / Not sure. Do not require the customer to invent a baseline.

**q28 — When would the facility ideally begin addressing the priority challenge?** Single-select: Immediate / within 30 days; 1–3 months; 3–6 months; 6–12 months; Exploratory / no fixed timeline; Unknown / Not sure.

**q29 — What constraints could affect implementation? Select all that apply.** Multi-select: Budget; IT policies / approvals; OT cybersecurity; Network / remote access; Staffing / available resources; Vendor dependencies; Data ownership / licensing; Equipment downtime / access windows; Competing priorities; Other (specify); No known constraints; Unknown / Not sure. Optional `constraint_notes`.

**q30 — Which roles should participate in reviewing the assessment findings and next steps? Select all that apply.** Multi-select: Plant / operations management; Manufacturing / process engineering; Controls / automation; Maintenance / reliability; IT / OT / cybersecurity; Continuous improvement / quality; Corporate digital / data; Finance / project sponsor; Other (specify). Optional `participant_notes`.

## Acceptance checks
1. Exactly six sections with 5, 5, 6, 6, 4, 4 questions respectively; exactly 30 unique IDs in ascending order.
2. q03, q06, q11, q12, q14, q22, q23, q26, q29, q30 are multi-select. q06 max three, q07 one of q06, q13 repeatable max five.
3. q13 vendor/platform optional; connection method and access status include Unknown and are optional.
4. q17 history, q18 frequency, q19 timestamp synchronization, q20 identifier consistency, q21 production context, q22 quality issues.
5. q23 analytics tools, q24 KPI validation, q25 AI experience, q26 capabilities.
6. q27 outcome + baseline, q28 timeframe, q29 constraints, q30 review participants.
7. Drafts permit missing answers; submitted answers are validated server-side in a later phase; Unknown is not confused with missing.
8. No USD-specific or budget-band question, no mandatory MES question, no assumption that AI is appropriate.
