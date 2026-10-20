using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Services.Assessments.Reports;
using Xunit;

namespace SentinelMfgSuite.Web.Tests;

public sealed class AssessmentReportTransitionPolicyTests
{
    private static readonly AssessmentReportRevisionStatus[] All = Enum.GetValues<AssessmentReportRevisionStatus>();

    private static readonly HashSet<(AssessmentReportRevisionStatus, AssessmentReportRevisionStatus)> Supported = new()
    {
        (AssessmentReportRevisionStatus.Draft, AssessmentReportRevisionStatus.InReview),
        (AssessmentReportRevisionStatus.InReview, AssessmentReportRevisionStatus.Approved),
        (AssessmentReportRevisionStatus.InReview, AssessmentReportRevisionStatus.Rejected),
        (AssessmentReportRevisionStatus.Approved, AssessmentReportRevisionStatus.Rejected),
        (AssessmentReportRevisionStatus.Approved, AssessmentReportRevisionStatus.Issued),
        (AssessmentReportRevisionStatus.Issued, AssessmentReportRevisionStatus.Superseded)
    };

    public static IEnumerable<object[]> AllPairs()
        => All.SelectMany(from => All.Select(to => new object[] { from, to }));

    [Fact]
    public void Matrix_Covers_Exactly_36_Combinations()
    {
        Assert.Equal(6, All.Length);
        Assert.Equal(36, AllPairs().Count());
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Evaluate_Matches_Matrix_For_Every_Combination(AssessmentReportRevisionStatus from, AssessmentReportRevisionStatus to)
    {
        var result = AssessmentReportTransitionPolicy.Evaluate(from, to);

        if (Supported.Contains((from, to)))
        {
            Assert.True(result.Succeeded);
            Assert.Equal(AssessmentReportTransitionFailure.None, result.Failure);
            Assert.Equal(from == AssessmentReportRevisionStatus.Issued && to == AssessmentReportRevisionStatus.Superseded, result.RequiresReplacementIssuance);
        }
        else
        {
            Assert.False(result.Succeeded);
            Assert.Equal(AssessmentReportTransitionFailure.InvalidTransition, result.Failure);
            Assert.False(result.RequiresReplacementIssuance);
        }
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Evaluate_Is_Deterministic(AssessmentReportRevisionStatus from, AssessmentReportRevisionStatus to)
        => Assert.Equal(AssessmentReportTransitionPolicy.Evaluate(from, to), AssessmentReportTransitionPolicy.Evaluate(from, to));

    [Fact]
    public void Exactly_Six_Transitions_Are_Supported()
        => Assert.Equal(6, AllPairs().Count(p => AssessmentReportTransitionPolicy.Evaluate((AssessmentReportRevisionStatus)p[0], (AssessmentReportRevisionStatus)p[1]).Succeeded));

    [Fact]
    public void Self_Transitions_Are_Invalid()
    {
        foreach (var status in All)
        {
            Assert.Equal(AssessmentReportTransitionFailure.InvalidTransition, AssessmentReportTransitionPolicy.Evaluate(status, status).Failure);
        }
    }

    [Theory]
    [InlineData(AssessmentReportRevisionStatus.Rejected)]
    [InlineData(AssessmentReportRevisionStatus.Superseded)]
    public void Terminal_States_Have_No_Outgoing_Transitions(AssessmentReportRevisionStatus terminal)
    {
        Assert.True(AssessmentReportTransitionPolicy.IsTerminal(terminal));
        Assert.All(All, to => Assert.False(AssessmentReportTransitionPolicy.Evaluate(terminal, to).Succeeded));
    }

    [Fact]
    public void Non_Terminal_States_Are_Not_Terminal()
    {
        foreach (var status in new[] { AssessmentReportRevisionStatus.Draft, AssessmentReportRevisionStatus.InReview, AssessmentReportRevisionStatus.Approved, AssessmentReportRevisionStatus.Issued })
        {
            Assert.False(AssessmentReportTransitionPolicy.IsTerminal(status));
        }
    }

    [Fact]
    public void Approved_To_Superseded_And_Anything_To_Draft_Are_Invalid()
    {
        Assert.False(AssessmentReportTransitionPolicy.Evaluate(AssessmentReportRevisionStatus.Approved, AssessmentReportRevisionStatus.Superseded).Succeeded);
        Assert.All(All, from => Assert.False(AssessmentReportTransitionPolicy.Evaluate(from, AssessmentReportRevisionStatus.Draft).Succeeded));
    }

    [Fact]
    public void Undefined_Status_Values_Are_Invalid()
    {
        var undefined = (AssessmentReportRevisionStatus)99;
        Assert.Equal(AssessmentReportTransitionFailure.InvalidTransition, AssessmentReportTransitionPolicy.Evaluate(undefined, AssessmentReportRevisionStatus.Draft).Failure);
        Assert.Equal(AssessmentReportTransitionFailure.InvalidTransition, AssessmentReportTransitionPolicy.Evaluate(AssessmentReportRevisionStatus.Draft, undefined).Failure);
    }

    [Fact]
    public void Only_Rejected_Requires_Reason()
        => Assert.All(All, status => Assert.Equal(status == AssessmentReportRevisionStatus.Rejected, AssessmentReportTransitionPolicy.RequiresReason(status)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Rejection_Requires_Nonempty_Reason(string? reason)
    {
        var result = AssessmentReportTransitionPolicy.Evaluate(AssessmentReportRevisionStatus.InReview, AssessmentReportRevisionStatus.Rejected, reason);
        Assert.False(result.Succeeded);
        Assert.Equal(AssessmentReportTransitionFailure.InvalidReason, result.Failure);
    }

    [Fact]
    public void Rejection_Reason_Is_Trimmed()
    {
        var result = AssessmentReportTransitionPolicy.Evaluate(AssessmentReportRevisionStatus.Approved, AssessmentReportRevisionStatus.Rejected, "  Needs more evidence.  ");
        Assert.True(result.Succeeded);
        Assert.Equal("Needs more evidence.", result.NormalizedReason);
    }

    [Fact]
    public void Rejection_Reason_Length_Limit_Is_2000_After_Trimming()
    {
        Assert.Equal(2000, AssessmentReportTransitionPolicy.MaxReasonLength);
        var atLimit = " " + new string('a', 2000) + " ";
        var overLimit = new string('a', 2001);

        Assert.True(AssessmentReportTransitionPolicy.Evaluate(AssessmentReportRevisionStatus.InReview, AssessmentReportRevisionStatus.Rejected, atLimit).Succeeded);
        Assert.Equal(AssessmentReportTransitionFailure.InvalidReason,
            AssessmentReportTransitionPolicy.Evaluate(AssessmentReportRevisionStatus.InReview, AssessmentReportRevisionStatus.Rejected, overLimit).Failure);
    }

    [Fact]
    public void Invalid_Transition_Takes_Precedence_Over_Reason_Validation()
    {
        var result = AssessmentReportTransitionPolicy.Evaluate(AssessmentReportRevisionStatus.Draft, AssessmentReportRevisionStatus.Rejected, null);
        Assert.Equal(AssessmentReportTransitionFailure.InvalidTransition, result.Failure);
    }

    [Fact]
    public void Reason_Is_Not_Required_For_Other_Targets()
    {
        var result = AssessmentReportTransitionPolicy.Evaluate(AssessmentReportRevisionStatus.Draft, AssessmentReportRevisionStatus.InReview, null);
        Assert.True(result.Succeeded);
        Assert.Null(result.NormalizedReason);
    }

    [Fact]
    public void Rejection_Returns_Normalized_Reason_At_Exact_Limit_After_Trim()
    {
        var body = new string('b', AssessmentReportTransitionPolicy.MaxReasonLength);
        var result = AssessmentReportTransitionPolicy.Evaluate(AssessmentReportRevisionStatus.InReview, AssessmentReportRevisionStatus.Rejected, "\t " + body + " \r\n");
        Assert.True(result.Succeeded);
        Assert.Equal(body, result.NormalizedReason);
    }

    public static IEnumerable<object[]> NonRejectionPermittedPairs()
    {
        yield return new object[] { AssessmentReportRevisionStatus.Draft, AssessmentReportRevisionStatus.InReview };
        yield return new object[] { AssessmentReportRevisionStatus.InReview, AssessmentReportRevisionStatus.Approved };
        yield return new object[] { AssessmentReportRevisionStatus.Approved, AssessmentReportRevisionStatus.Issued };
        yield return new object[] { AssessmentReportRevisionStatus.Issued, AssessmentReportRevisionStatus.Superseded };
    }

    [Theory]
    [MemberData(nameof(NonRejectionPermittedPairs))]
    public void Non_Rejection_Transitions_Ignore_Supplied_Reason(AssessmentReportRevisionStatus from, AssessmentReportRevisionStatus to)
    {
        foreach (var reason in new string?[] { null, "", "   ", "  Some text  ", new string('x', AssessmentReportTransitionPolicy.MaxReasonLength + 500) })
        {
            var result = AssessmentReportTransitionPolicy.Evaluate(from, to, reason);
            Assert.True(result.Succeeded);
            Assert.Equal(AssessmentReportTransitionFailure.None, result.Failure);
            Assert.Null(result.NormalizedReason);
            Assert.Equal(to == AssessmentReportRevisionStatus.Superseded, result.RequiresReplacementIssuance);
        }
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Invalid_Transitions_Report_InvalidTransition_Regardless_Of_Reason(AssessmentReportRevisionStatus from, AssessmentReportRevisionStatus to)
    {
        if (Supported.Contains((from, to)))
        {
            return;
        }

        foreach (var reason in new string?[] { null, "", "valid", new string('x', 5000) })
        {
            var result = AssessmentReportTransitionPolicy.Evaluate(from, to, reason);
            Assert.False(result.Succeeded);
            Assert.Equal(AssessmentReportTransitionFailure.InvalidTransition, result.Failure);
            Assert.Null(result.NormalizedReason);
        }
    }

    [Fact]
    public void Reason_Evaluation_Is_Deterministic()
    {
        var first = AssessmentReportTransitionPolicy.Evaluate(AssessmentReportRevisionStatus.Approved, AssessmentReportRevisionStatus.Rejected, "  why  ");
        var second = AssessmentReportTransitionPolicy.Evaluate(AssessmentReportRevisionStatus.Approved, AssessmentReportRevisionStatus.Rejected, "  why  ");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Failure_Codes_Include_All_Required_Contracts()
    {
        var names = Enum.GetNames<AssessmentReportTransitionFailure>();
        foreach (var required in new[] { "InvalidTransition", "Forbidden", "NotFound", "ConcurrencyConflict", "ValidationFailed", "InvalidReason" })
        {
            Assert.Contains(required, names);
        }
    }
}
