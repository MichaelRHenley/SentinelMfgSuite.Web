using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models.Assessments;
using SentinelMfgSuite.Web.Models.Assessments.Questionnaire;

namespace SentinelMfgSuite.Web.Services.Assessments;

public sealed class AssessmentProvisioningService : IAssessmentProvisioningService
{
    private static readonly TimeSpan DefaultInvitationExpiration = TimeSpan.FromDays(7);

    private readonly ApplicationDbContext _db;
    private readonly IAssessmentInvitationDeliveryService? _deliveryService;
    private readonly IAssessmentInvitationIssuanceTransactionParticipant _issuanceParticipant;

    public AssessmentProvisioningService(ApplicationDbContext db)
        : this(db, null, null)
    {
    }

    public AssessmentProvisioningService(ApplicationDbContext db, IAssessmentInvitationIssuanceTransactionParticipant? issuanceParticipant)
        : this(db, null, issuanceParticipant)
    {
    }

    public AssessmentProvisioningService(
        ApplicationDbContext db,
        IAssessmentInvitationDeliveryService? deliveryService,
        IAssessmentInvitationIssuanceTransactionParticipant? issuanceParticipant)
        : this(db, deliveryService, issuanceParticipant, null)
    {
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AssessmentProvisioningService(
        ApplicationDbContext db,
        IAssessmentInvitationDeliveryService? deliveryService,
        IAssessmentInvitationIssuanceTransactionParticipant? issuanceParticipant,
        Microsoft.Extensions.Options.IOptions<SentinelMfgSuite.Web.Services.Assessments.Payments.AssessmentPricingOptions>? pricing)
    {
        _db = db;
        _deliveryService = deliveryService;
        _issuanceParticipant = issuanceParticipant ?? new AssessmentInvitationIssuanceService(db);
        _pricing = pricing?.Value;
        _paymentPolicy = _pricing?.EnforcePayment == true ? AssessmentPaymentPolicy.Required : AssessmentPaymentPolicy.LegacyExempt;
    }

    private readonly AssessmentPaymentPolicy _paymentPolicy;
    private readonly SentinelMfgSuite.Web.Services.Assessments.Payments.AssessmentPricingOptions? _pricing;

    private AssessmentProvisioningResult? ValidatePricingIfRequired()
    {
        if (_paymentPolicy != AssessmentPaymentPolicy.Required) return null;
        var error = _pricing!.Validate();
        return error is null ? null : AssessmentProvisioningResult.Failure("PricingNotConfigured", error);
    }

    private AssessmentProvisioningResult? ValidateBillingIfRequired(AssessmentProvisioningRequest request)
    {
        if (_paymentPolicy != AssessmentPaymentPolicy.Required) return null;
        var errors = SentinelMfgSuite.Web.Services.Assessments.Payments.AssessmentBillingValidator.Validate(
            request.CustomerOrganization, request.BillingContactName, request.BillingContactEmail);
        return errors.Count == 0 ? null : AssessmentProvisioningResult.Failure("BillingInformationInvalid", string.Join(" ", errors.Select(e => e.Message)));
    }

    private void StageOrderIfRequired(Assessment assessment, AssessmentProvisioningRequest request)
    {
        if (_paymentPolicy != AssessmentPaymentPolicy.Required) return;
        SentinelMfgSuite.Web.Services.Assessments.Payments.AssessmentOrderService.StageOrder(
            _db, _pricing!, assessment.Id,
            request.CustomerOrganization!,
            request.BillingContactName!,
            request.BillingContactEmail!,
            AssessmentPaymentMethod.BankTransfer,
            request.OwnerUserId);
    }

    public async Task<AssessmentProvisioningResult> ProvisionAsync(AssessmentProvisioningRequest request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return AssessmentProvisioningResult.Failure("InvalidRequest", "Provisioning request is required.");
        }

        if (request.InitiationSource == AssessmentProvisioningInitiationSource.AutomaticPurchase)
        {
            if (string.IsNullOrWhiteSpace(request.ExternalPurchaseReference))
            {
                return AssessmentProvisioningResult.Failure(
                    "ExternalPurchaseReferenceRequired",
                    "Automatic purchase provisioning requires an external purchase reference.");
            }

            return AssessmentProvisioningResult.Failure(
                "AutomaticPurchaseNotReady",
                "Automatic purchase provisioning is not implemented yet.");
        }

        if (request.InitiationSource == AssessmentProvisioningInitiationSource.InternalDraftAdmin)
        {
            return await ProvisionInternalDraftAsync(request, ct);
        }

        if (request.InitiationSource != AssessmentProvisioningInitiationSource.ManualAdmin)
        {
            return AssessmentProvisioningResult.Failure("UnsupportedInitiationSource", "The provisioning initiation source is not supported.");
        }

        if (_paymentPolicy == AssessmentPaymentPolicy.Required)
        {
            return await ProvisionInternalDraftAsync(request, ct);
        }

        if (_deliveryService is null)
        {
            return AssessmentProvisioningResult.Failure(
                "DeliveryServiceUnavailable",
                "Invitation delivery service is required for manual provisioning.");
        }

        var atomic = await ProvisionManualWithInvitationAtomicAsync(request, DefaultInvitationExpiration, ct);
        if (!atomic.Succeeded || atomic.AssessmentId is null || atomic.InvitationId is null || string.IsNullOrWhiteSpace(atomic.InvitationToken) || atomic.InvitationExpiresUtc is null)
        {
            return AssessmentProvisioningResult.Failure(
                atomic.FailureCode ?? "ProvisioningFailed",
                atomic.FailureMessage ?? "Manual provisioning failed.");
        }

        var deliveryResult = await _deliveryService.DeliverAsync(new AssessmentInvitationDeliveryRequest(
            atomic.AssessmentId.Value,
            atomic.InvitationId.Value,
            request.CustomerContactEmail,
            atomic.InvitationToken,
            atomic.InvitationExpiresUtc.Value), ct);

        if (!deliveryResult.Succeeded)
        {
            return AssessmentProvisioningResult.Failure(
                deliveryResult.FailureCode ?? "EmailDeliveryFailed",
                deliveryResult.FailureMessage ?? "Invitation delivery failed. Please retry or contact support.",
                atomic.AssessmentId,
                atomic.InvitationId);
        }

        return AssessmentProvisioningResult.Success(atomic.AssessmentId.Value, atomic.InvitationId.Value);
    }

    private async Task<AssessmentProvisioningResult> ProvisionInternalDraftAsync(AssessmentProvisioningRequest request, CancellationToken ct)
    {
        var pricingFailure = ValidatePricingIfRequired();
        if (pricingFailure is not null) return pricingFailure;
        var billingFailure = ValidateBillingIfRequired(request);
        if (billingFailure is not null) return billingFailure;

        var nowUtc = DateTime.UtcNow;

        var assessment = new Assessment
        {
            Id = Guid.NewGuid(),
            Title = request.AssessmentTitle,
            SiteName = request.SiteName,
            ContactEmail = request.CustomerContactEmail,
            Status = AssessmentStatus.Draft,
            PaymentPolicy = _paymentPolicy,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
            OwnerUserId = request.OwnerUserId,
            QuestionnaireVersion = QuestionnaireDefinitionResolver.CurrentVersion
        };

        _db.Set<Assessment>().Add(assessment);
        StageOrderIfRequired(assessment, request);
        await _db.SaveChangesAsync(ct);

        return AssessmentProvisioningResult.Success(assessment.Id);
    }

    internal async Task<AssessmentManualProvisioningAtomicResult> ProvisionManualWithInvitationAtomicAsync(
        AssessmentProvisioningRequest request,
        TimeSpan? invitationExpiresIn = null,
        CancellationToken ct = default)
    {
        if (request is null)
        {
            return AssessmentManualProvisioningAtomicResult.Failure("InvalidRequest", "Provisioning request is required.");
        }

        if (request.InitiationSource != AssessmentProvisioningInitiationSource.ManualAdmin)
        {
            return AssessmentManualProvisioningAtomicResult.Failure("UnsupportedInitiationSource", "The provisioning initiation source is not supported.");
        }

        var nowUtc = DateTime.UtcNow;
        var expiresIn = invitationExpiresIn ?? DefaultInvitationExpiration;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var assessment = new Assessment
            {
                Id = Guid.NewGuid(),
                Title = request.AssessmentTitle,
                SiteName = request.SiteName,
                ContactEmail = request.CustomerContactEmail,
                Status = AssessmentStatus.Draft,
                PaymentPolicy = _paymentPolicy,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
                OwnerUserId = request.OwnerUserId,
                QuestionnaireVersion = QuestionnaireDefinitionResolver.CurrentVersion
            };

            _db.Set<Assessment>().Add(assessment);
            await _db.SaveChangesAsync(ct);

            var issuance = await _issuanceParticipant.IssueWithinExistingTransactionAsync(assessment.Id, expiresIn, ct);
            if (!issuance.Succeeded || issuance.InvitationId is null || issuance.ExpiresUtc is null || string.IsNullOrWhiteSpace(issuance.InvitationToken))
            {
                await tx.RollbackAsync(ct);
                return AssessmentManualProvisioningAtomicResult.Failure(
                    issuance.FailureCode ?? "InvitationIssuanceFailed",
                    issuance.FailureMessage ?? "Invitation issuance failed.");
            }

            await tx.CommitAsync(ct);
            return AssessmentManualProvisioningAtomicResult.Success(
                assessment.Id,
                issuance.InvitationId.Value,
                issuance.InvitationToken,
                issuance.ExpiresUtc.Value);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
