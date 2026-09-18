using ClaimsEngine.Api.Auth;
using ClaimsEngine.Api.Contracts;
using ClaimsEngine.Application.Claims;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Policies;

namespace ClaimsEngine.Api.Validation;

public sealed class CreatePolicyRequestValidator : IRequestValidator<CreatePolicyRequest>
{
    public const int MaxPolicyNumberLength = 64;

    public ValidationErrors Validate(CreatePolicyRequest request)
    {
        var errors = new ValidationErrors();

        if (string.IsNullOrWhiteSpace(request.PolicyNumber))
        {
            errors.Add("policyNumber", "policyNumber is required.");
        }
        else if (request.PolicyNumber.Length > MaxPolicyNumberLength)
        {
            errors.Add("policyNumber", $"policyNumber must be at most {MaxPolicyNumberLength} characters.");
        }

        if (request.HolderId is null || request.HolderId == Guid.Empty)
        {
            errors.Add("holderId", "holderId is required and must be a non-empty GUID.");
        }

        if (!EnumNames.IsValid<CoverageType>(request.CoverageType))
        {
            errors.Add("coverageType", $"coverageType must be one of: {EnumNames.Allowed<CoverageType>()}.");
        }

        if (request.CoverageLimit is not { } limit)
        {
            errors.Add("coverageLimit", "coverageLimit is required.");
        }
        else if (limit <= 0)
        {
            errors.Add("coverageLimit", "coverageLimit must be greater than zero.");
        }
        else if (!Amounts.HasAtMostTwoDecimals(limit))
        {
            errors.Add("coverageLimit", "coverageLimit must have at most two decimals.");
        }

        if (request.Deductible is not { } deductible)
        {
            errors.Add("deductible", "deductible is required.");
        }
        else if (deductible < 0)
        {
            errors.Add("deductible", "deductible cannot be negative.");
        }
        else if (!Amounts.HasAtMostTwoDecimals(deductible))
        {
            errors.Add("deductible", "deductible must have at most two decimals.");
        }
        else if (request.CoverageLimit is { } l && deductible >= l)
        {
            errors.Add("deductible", "deductible must be lower than coverageLimit.");
        }

        if (request.EffectiveFrom is null)
        {
            errors.Add("effectiveFrom", "effectiveFrom is required (yyyy-MM-dd).");
        }

        if (request.EffectiveTo is null)
        {
            errors.Add("effectiveTo", "effectiveTo is required (yyyy-MM-dd).");
        }
        else if (request.EffectiveFrom is { } from && request.EffectiveTo <= from)
        {
            errors.Add("effectiveTo", "effectiveTo must be after effectiveFrom.");
        }

        return errors;
    }
}

public sealed class SubmitClaimRequestValidator : IRequestValidator<SubmitClaimRequest>
{
    public ValidationErrors Validate(SubmitClaimRequest request)
    {
        var errors = new ValidationErrors();

        if (request.PolicyId is null || request.PolicyId == Guid.Empty)
        {
            errors.Add("policyId", "policyId is required and must be a non-empty GUID.");
        }

        if (!IncidentDates.TryParse(request.IncidentDate, out _))
        {
            errors.Add("incidentDate", "incidentDate must be a date (yyyy-MM-dd) or an ISO-8601 date-time with offset.");
        }

        if (request.ClaimedAmount is not { } amount)
        {
            errors.Add("claimedAmount", "claimedAmount is required.");
        }
        else if (amount <= 0)
        {
            errors.Add("claimedAmount", "claimedAmount must be greater than zero.");
        }
        else if (!Amounts.HasAtMostTwoDecimals(amount))
        {
            errors.Add("claimedAmount", "claimedAmount must have at most two decimals.");
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            errors.Add("description", "description is required.");
        }
        else if (request.Description.Length > Claim.MaxDescriptionLength)
        {
            errors.Add("description", $"description must be at most {Claim.MaxDescriptionLength} characters.");
        }

        return errors;
    }
}

public sealed class RejectClaimRequestValidator : IRequestValidator<RejectClaimRequest>
{
    public ValidationErrors Validate(RejectClaimRequest request)
    {
        var errors = new ValidationErrors();

        if (!EnumNames.IsValid<RejectionReason>(request.Reason))
        {
            errors.Add("reason", $"reason must be one of: {EnumNames.Allowed<RejectionReason>()}.");
        }
        else if (string.Equals(request.Reason?.Trim(), nameof(RejectionReason.Other), StringComparison.OrdinalIgnoreCase)
                 && string.IsNullOrWhiteSpace(request.Note))
        {
            errors.Add("note", "note is required when reason is Other.");
        }

        Notes.Validate(request.Note, errors);
        return errors;
    }
}

public sealed class NoteRequestValidator : IRequestValidator<NoteRequest>
{
    public ValidationErrors Validate(NoteRequest request)
    {
        var errors = new ValidationErrors();
        Notes.Validate(request.Note, errors);
        return errors;
    }
}

public sealed class ClearFlagRequestValidator : IRequestValidator<ClearFlagRequest>
{
    public ValidationErrors Validate(ClearFlagRequest request)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(request.Note))
        {
            errors.Add("note", "note is required when clearing an investigation flag.");
        }

        Notes.Validate(request.Note, errors);
        return errors;
    }
}

public sealed class ListClaimsRequestValidator : IRequestValidator<ListClaimsRequest>
{
    public ValidationErrors Validate(ListClaimsRequest request)
    {
        var errors = new ValidationErrors();

        if (request.Status is not null && !EnumNames.IsValid<ClaimStatus>(request.Status))
        {
            errors.Add("status", $"status must be one of: {EnumNames.Allowed<ClaimStatus>()}.");
        }

        if (request.Page is < 1)
        {
            errors.Add("page", "page must be at least 1.");
        }

        if (request.PageSize is { } size && (size < 1 || size > ListClaimsHandler.MaxPageSize))
        {
            errors.Add("pageSize", $"pageSize must be between 1 and {ListClaimsHandler.MaxPageSize}.");
        }

        return errors;
    }
}

public sealed class DevTokenRequestValidator : IRequestValidator<DevTokenRequest>
{
    public const int MaxSubjectLength = 128;

    public ValidationErrors Validate(DevTokenRequest request)
    {
        var errors = new ValidationErrors();

        if (string.IsNullOrWhiteSpace(request.Subject))
        {
            errors.Add("subject", "subject is required (use the holderId GUID for claimants).");
        }
        else if (request.Subject.Length > MaxSubjectLength)
        {
            errors.Add("subject", $"subject must be at most {MaxSubjectLength} characters.");
        }

        if (Roles.TryParse(request.Role) is null)
        {
            errors.Add("role", $"role must be one of: {Roles.AllowedList}.");
        }

        return errors;
    }
}

internal static class Notes
{
    public static void Validate(string? note, ValidationErrors errors)
    {
        if (note is { Length: > Claim.MaxDescriptionLength })
        {
            errors.Add("note", $"note must be at most {Claim.MaxDescriptionLength} characters.");
        }
    }
}

public static class ValidatorRegistration
{
    public static IServiceCollection AddRequestValidators(this IServiceCollection services)
    {
        services.AddSingleton<IRequestValidator<CreatePolicyRequest>, CreatePolicyRequestValidator>();
        services.AddSingleton<IRequestValidator<SubmitClaimRequest>, SubmitClaimRequestValidator>();
        services.AddSingleton<IRequestValidator<RejectClaimRequest>, RejectClaimRequestValidator>();
        services.AddSingleton<IRequestValidator<NoteRequest>, NoteRequestValidator>();
        services.AddSingleton<IRequestValidator<ClearFlagRequest>, ClearFlagRequestValidator>();
        services.AddSingleton<IRequestValidator<ListClaimsRequest>, ListClaimsRequestValidator>();
        services.AddSingleton<IRequestValidator<DevTokenRequest>, DevTokenRequestValidator>();
        return services;
    }
}
