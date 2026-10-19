using System;
using System.Collections.Generic;

namespace SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

// Versioned, strongly-typed questionnaire definition for Phase 1
public static class QuestionnaireV1
{
    public static readonly QuestionnaireDefinition Definition = CreateDefinition();

    private static QuestionnaireDefinition CreateDefinition()
    {
        var sections = new List<SectionDefinition>
        {
            // Section 1 — Facility & Operations (q01–q05)
            new SectionDefinition("facility_ops", "Facility & Operations", "Context about the site and operations", new List<QuestionDefinition>
            {
                // q01 — Facility name and location (two text fields)
                new QuestionDefinition("q01", "Facility name and location.", QuestionType.Composite, isOptional: false, repeatableFields: new []{
                    new FieldDefinition("facility_name","Facility name", QuestionType.Text, IsOptional:false),
                    new FieldDefinition("facility_location","Facility location", QuestionType.Text, IsOptional:false)
                }),

                // q02 — Products or product families (optional long text)
                new QuestionDefinition("q02", "What products or product families are manufactured at this facility?", QuestionType.LongText, isOptional: true),

                // q03 — Manufacturing processes (multi-select)
                new QuestionDefinition("q03", "Which manufacturing processes operate at this facility?", QuestionType.MultiSelect, choices: new []{
                    Choice.O("discrete","Discrete manufacturing / assembly"), Choice.O("continuous","Continuous process manufacturing"), Choice.O("batch","Batch manufacturing"), Choice.O("packaging","Packaging / converting"), Choice.O("material_handling","Material handling / warehousing"), Choice.O("other","Other (specify)"), Choice.Unknown()
                }, isOptional: true, supplementalFields: new []{
                    SupplementalFieldDefinition.Text("other", "Other manufacturing process", triggerChoiceValue: "other")
                }),

                // q04 — Number of production lines (single-select)
                new QuestionDefinition("q04", "Approximately how many production lines or work centres are at this facility?", QuestionType.SingleSelect, choices: new []{
                    Choice.O("1-5","1–5"), Choice.O("6-10","6–10"), Choice.O("11-25","11–25"), Choice.O("25+","More than 25"), Choice.Unknown()
                }, isOptional: false),

                // q05 — Operating schedule (single-select)
                new QuestionDefinition("q05", "What is the facility's typical operating schedule?", QuestionType.SingleSelect, choices: new []{
                    Choice.O("one_shift","One shift"), Choice.O("two_shifts","Two shifts"), Choice.O("three_shifts","Three shifts"), Choice.O("24_7","24 hours / 7 days"), Choice.O("variable","Variable / seasonal"), Choice.Unknown()
                }, isOptional: false),
            }),

            // Section 2 — Business Challenges (q06–q10)
            new SectionDefinition("business_challenges", "Business Challenges", "Key business pain-points and priorities", new List<QuestionDefinition>
            {
                // q06 — top challenges (multi-select, max 3, min 1 on submission)
                new QuestionDefinition("q06", "What are the facility's most significant operational challenges? Select up to three.", QuestionType.MultiSelect, choices: new []{
                    Choice.O("unplanned_downtime","Unplanned downtime"), Choice.O("equipment_reliability","Equipment reliability / maintenance"), Choice.O("throughput","Throughput / bottlenecks"), Choice.O("quality","Quality / scrap / rework"), Choice.O("scheduling","Scheduling / changeovers"), Choice.O("manual_data","Manual data collection / reporting"), Choice.O("visibility","Production visibility / inaccurate reporting"), Choice.O("energy","Energy consumption / costs"), Choice.O("traceability","Traceability / compliance"), Choice.O("other","Other (specify)")
                }, isOptional: false, minSelections:1, maxSelections:3, supplementalFields: new []{
                    SupplementalFieldDefinition.Text("other", "Other operational challenge", triggerChoiceValue: "other")
                }),

                // q07 — highest priority (single-select sourced from q06 selections)
                new QuestionDefinition("q07", "Which one of the selected challenges is the highest priority for this assessment?", QuestionType.SingleSelect, choices: null, isOptional: false),

                // q08 — How identified/investigated (long text)
                new QuestionDefinition("q08", "How is this priority challenge currently identified, investigated, and reported?", QuestionType.LongText, isOptional: true),

                // q09 — current impact (selection + optional details)
                new QuestionDefinition("q09", "What is the current impact of this challenge?", QuestionType.Composite, isOptional: true, repeatableFields: new []{
                    new FieldDefinition("impact_choice","Impact (choice)", QuestionType.SingleSelect, IsOptional:false, Choices: new []{ Choice.O("measured","Measured / quantified"), Choice.O("estimated","Estimated"), Choice.O("significant_unquantified","Significant but not quantified"), Choice.Unknown() }),
                    new FieldDefinition("impact_details","Impact details", QuestionType.Text, IsOptional:true)
                }, maxOccurrences:1),

                // q10 — information needed (long text)
                new QuestionDefinition("q10", "What information would help your team make better decisions about this challenge?", QuestionType.LongText, isOptional: true),
            }),

            // Section 3 — Systems & Data Sources (q11–q16)
            new SectionDefinition("systems_data", "Systems & Data Sources", "Inventory of systems and data sources", new List<QuestionDefinition>
            {
                // q11 — systems used (multi-select)
                new QuestionDefinition("q11", "Which systems or technologies are currently used at this facility? Select all that apply.", QuestionType.MultiSelect, choices: new []{
                    Choice.O("plc","PLCs / industrial controllers"), Choice.O("scada","SCADA / HMI"), Choice.O("historian","Process historian"), Choice.O("mes","Manufacturing Execution System (MES)"), Choice.O("erp","Enterprise Resource Planning (ERP)"),
                    Choice.O("cmms","Computerized Maintenance Management System (CMMS)"), Choice.O("quality","Quality management systems"), Choice.O("database","SQL / other databases"), Choice.O("manual","Spreadsheets / manual records"), Choice.O("cloud_iot","Cloud / IoT platforms"), Choice.O("other","Other"), Choice.Unknown()
                }, isOptional: false, supplementalFields: new []{
                    SupplementalFieldDefinition.Text("other", "Other system or technology", triggerChoiceValue: "other")
                }),

                // q12 — systems relevant to priority challenge (multi-select including Unknown)
                new QuestionDefinition("q12", "Which of these systems are relevant to investigating the priority operational challenge?", QuestionType.MultiSelect, choices: new []{
                    Choice.O("plc","PLCs / industrial controllers"), Choice.O("scada","SCADA / HMI"), Choice.O("historian","Process historian"), Choice.O("mes","Manufacturing Execution System (MES)"), Choice.O("erp","Enterprise Resource Planning (ERP)"),
                    Choice.O("cmms","Computerized Maintenance Management System (CMMS)"), Choice.O("quality","Quality management systems"), Choice.O("database","SQL / other databases"), Choice.O("manual","Spreadsheets / manual records"), Choice.O("cloud_iot","Cloud / IoT platforms"), Choice.O("other","Other"), Choice.Unknown()
                }, isOptional: true),

                // q13 — list up to five data sources (repeatable)
                new QuestionDefinition("q13", "List up to five data sources potentially relevant to the priority challenge.", QuestionType.Repeatable, isOptional: true, maxOccurrences:5, repeatableFields: new []{
                    new FieldDefinition("source_name","Source name", QuestionType.Text, IsOptional:false),
                    new FieldDefinition("system_type","System type", QuestionType.SingleSelect, IsOptional:false, Choices: new []{
                        Choice.O("plc","PLC"), Choice.O("scada","SCADA"), Choice.O("historian","Historian"), Choice.O("mes","MES"), Choice.O("erp","ERP"), Choice.O("cmms","CMMS"), Choice.O("quality","Quality"), Choice.O("database","Database"), Choice.O("cloud_iot","Cloud/IoT"), Choice.O("manual","Spreadsheet/manual"), Choice.O("other","Other"), Choice.Unknown()
                    }),
                    new FieldDefinition("vendor_platform","Vendor/platform", QuestionType.Text, IsOptional:true),
                    new FieldDefinition("available_data","Available data", QuestionType.Text, IsOptional:true),
                    new FieldDefinition("connection_method","Connection method", QuestionType.SingleSelect, IsOptional:true, Choices: new []{
                        Choice.Unknown(), Choice.O("opc_ua","OPC UA"), Choice.O("mqtt","MQTT"), Choice.O("sql","SQL / database"), Choice.O("api","API"), Choice.O("file","File / export"), Choice.O("vendor_integration","Vendor-specific integration"), Choice.O("no_connection","No known connection")
                    }),
                    new FieldDefinition("access_status","Access status", QuestionType.SingleSelect, IsOptional:true, Choices: new []{
                        Choice.Unknown(), Choice.O("available","Available"), Choice.O("requires_internal","Requires internal approval"), Choice.O("requires_vendor","Requires vendor involvement"), Choice.O("unavailable","Unavailable")
                    }),
                }),

                // q14 — how data collected (multi-select)
                new QuestionDefinition("q14", "How is operational data currently collected or transferred?", QuestionType.MultiSelect, choices: new []{
                    Choice.O("auto_equipment","Automatically from equipment / controls"), Choice.O("scada_hist","SCADA / historian collection"), Choice.O("apis","APIs"), Choice.O("db_queries","Database queries / exports"), Choice.O("manual_entry","Manual operator entry"), Choice.O("paper","Paper forms / spreadsheets"), Choice.O("not_collected","Not currently collected"), Choice.Unknown()
                }, isOptional: false),

                // q15 — integration level + optional notes
                new QuestionDefinition("q15", "How integrated are these systems today?", QuestionType.Composite, isOptional: true, repeatableFields: new []{
                    new FieldDefinition("integration_level","Integration level", QuestionType.SingleSelect, IsOptional:false, Choices: new []{ Choice.O("mostly","Mostly integrated"), Choice.O("partially","Partially integrated"), Choice.O("isolated","Largely isolated"), Choice.O("manual_transfers","Primarily manual transfers"), Choice.Unknown() }),
                    new FieldDefinition("integration_notes","Integration notes", QuestionType.Text, IsOptional:true)
                }, maxOccurrences:1),

                // q16 — responsibility for systems / data access
                new QuestionDefinition("q16", "Who is responsible for maintaining these systems and approving data access?", QuestionType.LongText, isOptional: true),
            }),

            // Section 4 — Data Quality & Context (q17–q22)
            new SectionDefinition("data_quality", "Data Quality & Context", "Questions about data accuracy, completeness, and contextual metadata", new List<QuestionDefinition>
            {
                // q17 — historical data availability + retention
                new QuestionDefinition("q17", "Is historical operational data available?", QuestionType.Composite, isOptional: true, repeatableFields: new []{
                    new FieldDefinition("history_choice","Historical data availability", QuestionType.SingleSelect, IsOptional:false, Choices: new []{ Choice.O("most_relevant","Most relevant sources"), Choice.O("some_sources","Some sources"), Choice.O("real_time_only","Real-time data only"), Choice.O("no_historical","No historical data"), Choice.Unknown() }),
                    new FieldDefinition("retention","Retention", QuestionType.SingleSelect, IsOptional:true, Choices: new []{ Choice.O("<30d","Under 30 days"), Choice.O("1_6m","1–6 months"), Choice.O("6_12m","6–12 months"), Choice.O(">12m","Over 12 months"), Choice.O("varies","Varies by source"), Choice.Unknown() })
                }, maxOccurrences:1),

                // q18 — frequency + notes
                new QuestionDefinition("q18", "How frequently is operational data recorded or updated?", QuestionType.Composite, isOptional: true, repeatableFields: new []{
                    new FieldDefinition("frequency_choice","Frequency", QuestionType.SingleSelect, IsOptional:false, Choices: new []{ Choice.O("sub_second","Sub-second"), Choice.O("seconds","Every few seconds"), Choice.O("minutes","Every few minutes"), Choice.O("hourly","Hourly or less frequently"), Choice.O("event","Event-based"), Choice.O("varies","Varies by source"), Choice.O("manual","Manual only"), Choice.Unknown() }),
                    new FieldDefinition("frequency_notes","Frequency notes", QuestionType.Text, IsOptional:true)
                }, maxOccurrences:1),

                // q19 — timestamps synchronized
                new QuestionDefinition("q19", "Are timestamps synchronized across systems?", QuestionType.SingleSelect, choices: new []{ Choice.O("yes","Yes"), Choice.O("partial","Partially"), Choice.O("no","No"), Choice.Unknown() }, isOptional: true),

                // q20 — identifiers consistency
                new QuestionDefinition("q20", "Are equipment, production-line and variable identifiers consistent and documented?", QuestionType.SingleSelect, choices: new []{ Choice.O("standardized","Standardized and documented"), Choice.O("partial","Partially standardized"), Choice.O("inconsistent","Inconsistent"), Choice.O("none","No established convention"), Choice.Unknown() }, isOptional: true),

                // q21 — association with production orders/batches
                new QuestionDefinition("q21", "Can equipment data be associated with production orders, products, batches or operating conditions?", QuestionType.SingleSelect, choices: new []{ Choice.O("automatic","Automatically"), Choice.O("partial","Partially"), Choice.O("manual_match","Through manual matching"), Choice.O("no","No"), Choice.Unknown() }, isOptional: true),

                // q22 — data-quality problems (multi-select) + optional notes
                new QuestionDefinition("q22", "What data-quality problems are known or suspected? Select all that apply.", QuestionType.MultiSelect, choices: new []{
                    Choice.O("missing","Missing data / gaps"), Choice.O("incorrect_values","Incorrect values"), Choice.O("unreliable_sensors","Unreliable / disconnected sensors"), Choice.O("inconsistent_naming","Inconsistent naming"), Choice.O("timestamp_issues","Incorrect / inconsistent timestamps"), Choice.O("manual_errors","Manual-entry errors"), Choice.O("downtime_codes","Inaccurate downtime / reason codes"), Choice.O("duplicates","Duplicate records"), Choice.O("inaccessible","Data inaccessible"), Choice.O("other","Other (specify)"), Choice.O("no_known","No known issues"), Choice.Unknown()
                }, isOptional: true, requiresDiscovery: true, supplementalFields: new []{
                    SupplementalFieldDefinition.Text("quality_notes", "Quality notes"),
                    SupplementalFieldDefinition.Text("other", "Other data-quality issue", triggerChoiceValue: "other")
                }),
            }),

            // Section 5 — Analytics & AI Readiness (q23–q26)
            new SectionDefinition("analytics_ai", "Analytics & AI Readiness", "Assess current analytics capabilities and AI readiness", new List<QuestionDefinition>
            {
                // q23 — tools used for reporting/analytics (multi-select)
                new QuestionDefinition("q23", "What tools are currently used for reporting or analytics? Select all that apply.", QuestionType.MultiSelect, choices: new []{
                    Choice.O("excel","Excel / spreadsheets"), Choice.O("power_bi","Power BI"), Choice.O("tableau","Tableau / Qlik / other BI tools"), Choice.O("scada_dash","SCADA / HMI dashboards"), Choice.O("historian_reporting","Historian reporting"), Choice.O("mes_erp_reporting","MES / ERP reporting"), Choice.O("custom_apps","Custom applications"), Choice.O("cloud_analytics","Cloud analytics"), Choice.O("other","Other (specify)"), Choice.O("none","None"), Choice.Unknown()
                }, isOptional: true, supplementalFields: new []{
                    SupplementalFieldDefinition.Text("other", "Other reporting or analytics tool", triggerChoiceValue: "other")
                }),

                // q24 — KPI calculation/validation
                new QuestionDefinition("q24", "How are operational KPIs calculated and validated?", QuestionType.SingleSelect, choices: new []{ Choice.O("automated_validated","Automated and validated"), Choice.O("automated_partial","Automated but only partly validated"), Choice.O("mix","Mix of automated and manual"), Choice.O("manual","Primarily manual"), Choice.O("not_calculated","Not currently calculated"), Choice.Unknown() }, isOptional: true, supplementalFields: new []{
                    SupplementalFieldDefinition.Text("kpi_notes", "KPI notes")
                }),

                // q25 — AI/ML experience
                new QuestionDefinition("q25", "Has the facility used or evaluated AI / machine learning for manufacturing?", QuestionType.SingleSelect, choices: new []{ Choice.O("production","In production"), Choice.O("pilot","Pilot / proof of concept"), Choice.O("evaluated","Evaluated but not deployed"), Choice.O("planning","Planning / considering"), Choice.O("no","No"), Choice.Unknown() }, isOptional: true, supplementalFields: new []{
                    SupplementalFieldDefinition.Text("ai_experience_notes", "AI experience notes")
                }),

                // q26 — capabilities available (multi-select)
                new QuestionDefinition("q26", "Which internal or external capabilities are available to support a data or analytics initiative? Select all that apply.", QuestionType.MultiSelect, choices: new []{
                    Choice.O("controls","Controls / automation"), Choice.O("it_networking","IT / networking"), Choice.O("ot_cyber","OT cybersecurity"), Choice.O("integration","Software / systems integration"), Choice.O("data_engineering","Data engineering"), Choice.O("bi_reporting","BI / reporting"), Choice.O("ml_ds","Machine learning / data science"), Choice.O("subject_matter","Manufacturing / process subject-matter expertise"), Choice.O("external_vendors","External integrators / vendors"), Choice.Unknown()
                }, isOptional: true),
            }),

            // Section 6 — Objectives & Constraints (q27–q30)
            new SectionDefinition("objectives_constraints", "Objectives & Constraints", "Goals, constraints, and success criteria", new List<QuestionDefinition>
            {
                // q27 — desired outcome + baseline
                new QuestionDefinition("q27", "What measurable operational outcome would make this assessment valuable?", QuestionType.Composite, isOptional: false, repeatableFields: new []{
                    new FieldDefinition("desired_outcome","Desired outcome", QuestionType.LongText, IsOptional:false),
                    new FieldDefinition("baseline_available","Baseline available", QuestionType.SingleSelect, IsOptional:true, Choices: new []{ Choice.O("yes","Yes"), Choice.O("partial","Partially"), Choice.O("no","No"), Choice.Unknown() })
                }),

                // q28 — timeframe
                new QuestionDefinition("q28", "When would the facility ideally begin addressing the priority challenge?", QuestionType.SingleSelect, choices: new []{ Choice.O("immediate","Immediate / within 30 days"), Choice.O("1_3m","1–3 months"), Choice.O("3_6m","3–6 months"), Choice.O("6_12m","6–12 months"), Choice.O("exploratory","Exploratory / no fixed timeline"), Choice.Unknown() }, isOptional: true),

                // q29 — constraints (multi-select) + optional notes
                new QuestionDefinition("q29", "What constraints could affect implementation? Select all that apply.", QuestionType.MultiSelect, choices: new []{
                    Choice.O("budget","Budget"), Choice.O("it_policies","IT policies / approvals"), Choice.O("ot_cyber","OT cybersecurity"), Choice.O("network","Network / remote access"), Choice.O("staffing","Staffing / available resources"), Choice.O("vendor_deps","Vendor dependencies"), Choice.O("data_ownership","Data ownership / licensing"), Choice.O("downtime","Equipment downtime / access windows"), Choice.O("competing","Competing priorities"), Choice.O("other","Other (specify)"), Choice.O("no_known","No known constraints"), Choice.Unknown()
                }, isOptional: true, supplementalFields: new []{
                    SupplementalFieldDefinition.Text("constraint_notes", "Constraint notes"),
                    SupplementalFieldDefinition.Text("other", "Other constraint", triggerChoiceValue: "other")
                }),

                // q30 — participants (multi-select)
                new QuestionDefinition("q30", "Which roles should participate in reviewing the assessment findings and next steps? Select all that apply.", QuestionType.MultiSelect, choices: new []{
                    Choice.O("plant_ops","Plant / operations management"), Choice.O("process_eng","Manufacturing / process engineering"), Choice.O("controls","Controls / automation"), Choice.O("maintenance","Maintenance / reliability"), Choice.O("it_ot","IT / OT / cybersecurity"), Choice.O("ci_quality","Continuous improvement / quality"), Choice.O("corporate_digital","Corporate digital / data"), Choice.O("finance","Finance / project sponsor"), Choice.O("other","Other (specify)"), Choice.Unknown()
                }, isOptional: true, supplementalFields: new []{
                    SupplementalFieldDefinition.Text("participant_notes", "Participant notes"),
                    SupplementalFieldDefinition.Text("other", "Other participant role", triggerChoiceValue: "other")
                }),
            }),
        };

        return new QuestionnaireDefinition
        {
            SchemaVersion = "1.0",
            Title = "Sentinel Industrial Data & AI Readiness Assessment - Phase 1",
            Description = "Phase 1 questionnaire capturing business priorities, systems, and data quality for readiness assessment.",
            CreatedUtc = DateTime.UtcNow,
            Sections = sections
        };
    }
}

public record QuestionnaireDefinition
{
    public string SchemaVersion { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }
    public List<SectionDefinition> Sections { get; init; } = new();
}

public record SectionDefinition(string Id, string Title, string? Description, List<QuestionDefinition> Questions)
{
}

public enum QuestionType
{
    SingleSelect,
    MultiSelect,
    Text,
    LongText,
    Composite,
    Repeatable,
}

public record Choice
{
    public string Value { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public bool IsUnknown { get; init; }
    public bool RequiresDiscovery { get; init; }

    public static Choice O(string value, string label) => new Choice{ Value = value, Label = label };
    public static Choice Unknown() => new Choice{ Value = "unknown", Label = "Unknown / Not sure", IsUnknown = true };
}

public record FieldDefinition(string Id, string Label, QuestionType Type, bool IsOptional = true, Choice[]? Choices = null)
{
    public Choice[]? ChoicesArray => Choices;
}

public record SupplementalFieldDefinition(string Id, string Label, bool IsOptional = true, string? TriggerChoiceValue = null)
{
    public static SupplementalFieldDefinition Text(string id, string label, bool isOptional = true, string? triggerChoiceValue = null)
        => new(id, label, isOptional, triggerChoiceValue);
}

// Strongly-typed visibility/conditional definition for questionnaire rules.
public record ConditionDefinition(string QuestionId, string Operator, string ExpectedValue);

public class QuestionDefinition
{
    public string Id { get; }
    public string DisplayNumber { get; }
    public string Text { get; }
    public QuestionType Type { get; }
    public List<Choice>? Choices { get; }
    public bool IsOptional { get; }
    public ConditionDefinition? VisibilityCondition { get; }
    public bool RequiresDiscovery { get; }

    // Selection limits
    public int? MinSelections { get; }
    public int? MaxSelections { get; }

    // Repeatable specifics
    public FieldDefinition[]? RepeatableFields { get; }
    public int? MaxOccurrences { get; }

    // Supplemental text fields for single- and multi-select answers
    public SupplementalFieldDefinition[]? SupplementalFields { get; }

    public QuestionDefinition(string id, string text, QuestionType type, Choice[]? choices = null,
        bool isOptional = true, ConditionDefinition? visibilityCondition = null, int? minSelections = null, int? maxSelections = null,
        bool requiresDiscovery = false, FieldDefinition[]? repeatableFields = null, int? maxOccurrences = null,
        SupplementalFieldDefinition[]? supplementalFields = null, string? displayNumber = null)
    {
        Id = id;
        DisplayNumber = displayNumber ?? id.ToUpperInvariant();
        Text = text;
        Type = type;
        Choices = choices is null ? null : new List<Choice>(choices);
        IsOptional = isOptional;
        VisibilityCondition = visibilityCondition;
        MinSelections = minSelections;
        MaxSelections = maxSelections;
        RequiresDiscovery = requiresDiscovery;
        RepeatableFields = repeatableFields;
        MaxOccurrences = maxOccurrences;
        SupplementalFields = supplementalFields;
    }
}
