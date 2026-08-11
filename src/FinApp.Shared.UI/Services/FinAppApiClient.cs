using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinApp.Contracts;

namespace FinApp.Shared.UI.Services;

/// <summary>A friendly error from the API carrying the server's message and HTTP status.</summary>
public class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>A 402 from a Pro-gated endpoint (OPEN-BETA P4), carrying the blocked feature key. The server-side
/// backstop for the paywall; a properly-gated client raises the prompt before the call, so this is the fallback
/// path when a gate was skipped. Still an <see cref="ApiException"/>, so existing catch sites keep working.</summary>
public sealed class PaymentRequiredApiException(string feature, string message)
    : ApiException(HttpStatusCode.PaymentRequired, message)
{
    public string Feature { get; } = feature;
}

/// <summary>
/// Typed client over the FinApp sync API. Attaches the bearer <see cref="Token"/> to every call and
/// turns non-success responses into <see cref="ApiException"/> with the server's error message.
/// </summary>
public sealed class FinAppApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Set after login; cleared on logout. Null = anonymous calls (register/login).</summary>
    public string? Token { get; set; }

    /// <summary>
    /// Invoked once when a request comes back 401, given the access token that was used. The handler
    /// (wired by <see cref="AuthState"/>) should try to refresh <see cref="Token"/> and return true if the
    /// request is worth retrying. Lets an expired access token be renewed transparently, mid-session.
    /// </summary>
    public Func<string?, Task<bool>>? OnUnauthorized { get; set; }

    /// <summary>Raised when any call comes back 402 (a Pro feature reached on a Free plan), with the blocked
    /// feature key. MainLayout wires this to the upgrade prompt so a server refusal looks identical to a local gate.</summary>
    public event Action<string>? PaymentRequired;

    // --- Auth -------------------------------------------------------------
    public Task<AuthResponse> RegisterAsync(RegisterRequest req, CancellationToken ct = default) =>
        SendAsync<AuthResponse>(HttpMethod.Post, "/auth/register", req, ct);
    public Task<LoginResponse> LoginAsync(LoginRequest req, CancellationToken ct = default) =>
        SendAsync<LoginResponse>(HttpMethod.Post, "/auth/login", req, ct);
    public Task<AuthResponse> TwoFactorLoginAsync(string ticket, string code, CancellationToken ct = default) =>
        SendAsync<AuthResponse>(HttpMethod.Post, "/auth/2fa", new TwoFactorLoginRequest(ticket, code), ct);
    public Task ResendVerificationAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/auth/resend-verification", null, ct);
    public Task<TwoFactorSetupDto> SetupTwoFactorAsync(CancellationToken ct = default) =>
        SendAsync<TwoFactorSetupDto>(HttpMethod.Post, "/auth/2fa/setup", null, ct);
    public Task<TwoFactorRecoveryDto> ConfirmTwoFactorAsync(string code, CancellationToken ct = default) =>
        SendAsync<TwoFactorRecoveryDto>(HttpMethod.Post, "/auth/2fa/confirm", new TwoFactorCodeRequest(code), ct);
    public Task DisableTwoFactorAsync(string code, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/auth/2fa/disable", new TwoFactorCodeRequest(code), ct);
    public Task DeleteMyAccountAsync(string? twoFactorCode, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/me/delete", new DeleteAccountRequest(twoFactorCode), ct);
    public Task CancelMyDeletionAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/me/delete/cancel", null, ct);
    public Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
        SendAsync<AuthResponse>(HttpMethod.Post, "/auth/refresh", new RefreshRequest(refreshToken), ct);
    public Task LogoutAsync(string refreshToken, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/auth/logout", new LogoutRequest(refreshToken), ct);
    public Task<LoginResponse> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
        SendAsync<LoginResponse>(HttpMethod.Post, "/auth/exchange", new ExchangeCodeRequest(code), ct);
    public Task<UserDto> MeAsync(CancellationToken ct = default) =>
        SendAsync<UserDto>(HttpMethod.Get, "/me", null, ct);
    public Task<ExternalProvidersDto> GetProvidersAsync(CancellationToken ct = default) =>
        SendAsync<ExternalProvidersDto>(HttpMethod.Get, "/auth/providers", null, ct);

    // --- Consent (audit-logged) -------------------------------------------
    public Task<ConsentStatusDto> GetConsentAsync(string scope, Guid? accountId = null, CancellationToken ct = default) =>
        SendAsync<ConsentStatusDto>(HttpMethod.Get, $"/consent?scope={Uri.EscapeDataString(scope)}{(accountId is { } a ? $"&accountId={a}" : "")}", null, ct);
    public Task RecordConsentAsync(string scope, Guid? accountId, bool granted, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/consent", new RecordConsentRequest(scope, accountId, granted), ct);
    public Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/auth/password", new ChangePasswordRequest(currentPassword, newPassword), ct);
    // Anonymous: always resolves (the server never says whether the identifier matched an account).
    public Task ForgotPasswordAsync(string identifier, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/auth/password/forgot", new ForgotPasswordRequest(identifier), ct);
    public Task ResetPasswordAsync(string token, string newPassword, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/auth/password/reset", new ResetPasswordRequest(token, newPassword), ct);
    public Task UpdateAvatarAsync(string? dataUrl, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, "/me/avatar", new SetAvatarRequest(dataUrl), ct);
    public Task<Dictionary<Guid, string>> GetAccountAvatarsAsync(Guid accountId, CancellationToken ct = default) =>
        SendAsync<Dictionary<Guid, string>>(HttpMethod.Get, $"/accounts/{accountId}/avatars", null, ct);

    // --- Accounts ---------------------------------------------------------
    public Task<List<AccountSummaryDto>> GetAccountsAsync(CancellationToken ct = default) =>
        SendAsync<List<AccountSummaryDto>>(HttpMethod.Get, "/accounts", null, ct);
    public Task<AccountSummaryDto> CreateAccountAsync(CreateAccountRequest req, CancellationToken ct = default) =>
        SendAsync<AccountSummaryDto>(HttpMethod.Post, "/accounts", req, ct);
    public Task RenameAccountAsync(Guid id, string name, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"/accounts/{id}/name", new RenameAccountRequest(name), ct);
    // Path-B thin Account settings: read the editable settings + set the savings-rate target (percent 0..100).
    public Task<AccountSettingsDto> GetSettingsAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<AccountSettingsDto>(HttpMethod.Get, $"/accounts/{id}/settings", null, ct);
    public Task<MutationResultDto> SetSavingsTargetAsync(Guid id, decimal percent, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/savings-target", new SetSavingsTargetRequest(percent), ct);
    // Path-B thin Structure editor: read the account's categories/funds/contribution categories (writes below).
    public Task<StructureViewDto> GetStructureAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<StructureViewDto>(HttpMethod.Get, $"/accounts/{id}/structure", null, ct);
    // Path-B thin Achievements: the full catalogue (earned + locked with progress). Read-only.
    public Task<AchievementsViewDto> GetAchievementsAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<AchievementsViewDto>(HttpMethod.Get, $"/accounts/{id}/achievements", null, ct);
    // Path-B thin onboarding: read the getting-started checklist; dismiss the card.
    public Task<OnboardingViewDto> GetOnboardingAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<OnboardingViewDto>(HttpMethod.Get, $"/accounts/{id}/onboarding", null, ct);
    public Task<MutationResultDto> DismissOnboardingAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/onboarding/dismissed", null, ct);
    // Path-B thin notifications bell: the current-period domain-derived alerts. Read-only.
    public Task<NotificationsViewDto> GetNotificationsAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<NotificationsViewDto>(HttpMethod.Get, $"/accounts/{id}/notifications", null, ct);
    public Task DeleteAccountAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/accounts/{id}", null, ct);

    // --- Membership / archiving -------------------------------------------
    public async Task<LeaveAccountResult> LeaveAccountAsync(Guid id, Guid? newOwnerUserId, CancellationToken ct = default)
    {
        var res = await SendAsync<LeaveResultDto>(HttpMethod.Post, $"/accounts/{id}/leave", new LeaveAccountRequest(newOwnerUserId), ct);
        return Enum.TryParse<LeaveAccountResult>(res.Result, out var r) ? r : LeaveAccountResult.Left;
    }
    public Task RemoveMemberAsync(Guid id, Guid memberUserId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/accounts/{id}/members/{memberUserId}", null, ct);
    public Task TransferOwnershipAsync(Guid id, Guid newOwnerUserId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/accounts/{id}/transfer-ownership", new TransferOwnershipRequest(newOwnerUserId), ct);
    public Task<List<ArchivedAccountDto>> GetArchivedAccountsAsync(CancellationToken ct = default) =>
        SendAsync<List<ArchivedAccountDto>>(HttpMethod.Get, "/accounts/archived", null, ct);
    public Task ReactivateAccountAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/accounts/{id}/reactivate", null, ct);

    private record LeaveResultDto(string Result);

    // --- Snapshot ---------------------------------------------------------
    public Task<AccountSnapshot> GetSnapshotAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<AccountSnapshot>(HttpMethod.Get, $"/accounts/{id}/snapshot", null, ct);
    public Task<AccountSnapshot> SaveSnapshotAsync(Guid id, SaveAccountRequest req, CancellationToken ct = default) =>
        SendAsync<AccountSnapshot>(HttpMethod.Put, $"/accounts/{id}/snapshot", req, ct);

    // --- Command writes (Option-A cutover) --------------------------------
    // One method per mutation endpoint. All return MutationResultDto (new Version + affected entity id);
    // BudgetingState re-fetches the snapshot after each so the local aggregate reflects the server's result.

    public Task<MutationResultDto> BootstrapAccountAsync(Guid id, DateOnly? today = null, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/bootstrap", new BootstrapAccountRequest(today), ct);

    // Expenses
    public Task<MutationResultDto> AddExpenseAsync(Guid id, AddExpenseRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/expenses", req, ct);
    public Task<MutationResultDto> EditExpenseAsync(Guid id, Guid expenseId, EditExpenseRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/expenses/{expenseId}", req, ct);
    public Task<MutationResultDto> RemoveExpenseAsync(Guid id, Guid expenseId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete, $"/accounts/{id}/expenses/{expenseId}", null, ct);

    /// <summary>Report a client-side failure (OPEN-BETA B1). Anonymous-friendly — a crash can happen before
    /// sign-in — and the caller swallows failures, so this must not be relied on for anything but diagnostics.</summary>
    public Task ReportClientErrorAsync(ClientErrorReport report, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/client-errors", report, ct);

    /// <summary>Send user feedback (OPEN-BETA B2). Works signed-out — the landing page is where someone who chose
    /// not to sign up can tell us why. Unlike the error reporter, failures surface: losing what someone took the
    /// time to write is worth an error message.</summary>
    public Task SendFeedbackAsync(FeedbackRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/feedback", request, ct);

    /// <summary>Owner-only usage metrics (OPEN-BETA P2). 403 for non-admins — the gate is server-side.</summary>
    public Task<AdminMetricsDto> GetAdminMetricsAsync(CancellationToken ct = default) =>
        SendAsync<AdminMetricsDto>(HttpMethod.Get, "/admin/metrics", null, ct);

    /// <summary>The Plans screen data (OPEN-BETA P4). Reports Enabled=false while monetization is off.</summary>
    public Task<PlansDto> GetPlansAsync(CancellationToken ct = default) =>
        SendAsync<PlansDto>(HttpMethod.Get, "/plans", null, ct);

    /// <summary>Pricing for the landing page — anonymous, since it has to render before anyone has an account.</summary>
    public Task<PlansDto> GetPublicPlansAsync(CancellationToken ct = default) =>
        SendAsync<PlansDto>(HttpMethod.Get, "/plans/public", null, ct);

    /// <summary>Consented + approved reviews for the landing carousel (OPEN-BETA P1).</summary>
    public Task<List<PublicReviewDto>> GetPublicReviewsAsync(CancellationToken ct = default) =>
        SendAsync<List<PublicReviewDto>>(HttpMethod.Get, "/reviews/public", null, ct);

    /// <summary>How many free-beta seats are left. Anonymous — the landing page shows it before sign-up.</summary>
    public Task<BetaCapacityDto> GetBetaCapacityAsync(CancellationToken ct = default) =>
        SendAsync<BetaCapacityDto>(HttpMethod.Get, "/beta/capacity", null, ct);

    /// <summary>Admin-only: pin your own account to "free"/"pro", or null to clear. For testing the upgrade.</summary>
    public Task SetPlanOverrideAsync(string? plan, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/admin/plan-override", new PlanOverrideRequest(plan), ct);

    /// <summary>Admin-only: re-classify an account's cohort by email — the fix for a test account that landed in
    /// the lifetime-Pro beta cohort (e.g. one created via Google sign-in, where no +test alias is possible).</summary>
    public Task<CohortResultDto> SetCohortAsync(string email, string cohort, CancellationToken ct = default) =>
        SendAsync<CohortResultDto>(HttpMethod.Post, "/admin/cohort", new SetCohortRequest(email, cohort), ct);

    /// <summary>Admin-only: the review moderation queue (everything with public consent).</summary>
    public Task<List<AdminFeedbackDto>> GetAdminFeedbackAsync(CancellationToken ct = default) =>
        SendAsync<List<AdminFeedbackDto>>(HttpMethod.Get, "/admin/feedback", null, ct);

    /// <summary>Admin-only: approve or un-approve a review for the landing carousel.</summary>
    public Task ApproveReviewAsync(string id, bool approved, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/admin/feedback/{id}/approve", new ApproveReviewRequest(approved), ct);

    /// <summary>Begin an upgrade. 404s while monetization is off — the rails are unreachable during beta.</summary>
    public Task<CheckoutSessionDto> StartCheckoutAsync(BillingInterval interval, CancellationToken ct = default) =>
        SendAsync<CheckoutSessionDto>(HttpMethod.Post, "/billing/checkout", new CheckoutRequest(interval), ct);

    /// <summary>Complete a sandbox checkout (stands in for the provider webhook until one is wired).</summary>
    public Task CompleteSandboxCheckoutAsync(BillingInterval interval, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/billing/sandbox/complete", new CheckoutRequest(interval), ct);

    // Loan installments — one payment posting several linked rows (principal / interest / additional lines).
    public Task<MutationResultDto> LogInstallmentAsync(Guid id, LogInstallmentRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/installments", req, ct);
    public Task<MutationResultDto> RemoveInstallmentAsync(Guid id, Guid groupId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete, $"/accounts/{id}/installments/{groupId}", null, ct);

    // Deposits (income)
    public Task<MutationResultDto> AddDepositAsync(Guid id, AddDepositRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/deposits", req, ct);
    public Task<MutationResultDto> EditDepositAsync(Guid id, Guid depositId, EditDepositRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/deposits/{depositId}", req, ct);
    public Task<MutationResultDto> RemoveDepositAsync(Guid id, Guid depositId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete, $"/accounts/{id}/deposits/{depositId}", null, ct);

    // Savings money-movements
    public Task<MutationResultDto> AddSavingDepositAsync(Guid id, AddSavingDepositRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/savings/deposits", req, ct);
    public Task<MutationResultDto> EditSavingDepositAsync(Guid id, Guid allocationId, EditSavingDepositRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/savings/deposits/{allocationId}", req, ct);
    public Task<MutationResultDto> RemoveSavingDepositAsync(Guid id, Guid allocationId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete, $"/accounts/{id}/savings/deposits/{allocationId}", null, ct);
    public Task<MutationResultDto> SpendFromSavingsAsync(Guid id, SpendFromSavingsRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/savings/spend", req, ct);
    public Task<MutationResultDto> DisburseSavingAsync(Guid id, DisburseSavingRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/savings/disburse", req, ct);
    public Task<MutationResultDto> ConvertSavingToBudgetAsync(Guid id, ConvertSavingToBudgetRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/savings/to-budget", req, ct);
    public Task<MutationResultDto> MoveSavingsAsync(Guid id, MoveSavingsRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/savings/transfer", req, ct);
    public Task<MutationResultDto> RemoveSavingMovementAsync(Guid id, Guid allocationId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete, $"/accounts/{id}/savings/movements/{allocationId}", null, ct);

    // Savings buckets
    public Task<MutationResultDto> AddSavingBucketAsync(Guid id, SaveSavingBucketRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/savings/buckets", req, ct);
    public Task<MutationResultDto> SaveSavingBucketAsync(Guid id, Guid bucketId, SaveSavingBucketRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/savings/buckets/{bucketId}", req, ct);
    public Task<MutationResultDto> SetSavingBucketArchivedAsync(Guid id, Guid bucketId, bool archived, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/savings/buckets/{bucketId}/archived", new SetArchivedRequest(archived), ct);
    public Task<MutationResultDto> RemoveSavingBucketAsync(Guid id, Guid bucketId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete, $"/accounts/{id}/savings/buckets/{bucketId}", null, ct);

    // Account structure: categories, funds, contribution categories
    public Task<MutationResultDto> CreateCategoryAsync(Guid id, CreateCategoryRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/categories", req, ct);
    public Task<MutationResultDto> EditCategoryAsync(Guid id, Guid categoryId, EditCategoryRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/categories/{categoryId}", req, ct);
    public Task<MutationResultDto> SetCategoryArchivedAsync(Guid id, Guid categoryId, bool archived, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/categories/{categoryId}/archived", new SetArchivedRequest(archived), ct);
    /// <summary><paramref name="moveTo"/> re-files this category's (and its sub-categories') expenses there instead
    /// of the delete being refused because history references it. Null keeps the strict "nothing may point at it" rule.</summary>
    /// <summary>Edit both halves of an account-to-account transfer in one atomic two-account save. Addressed by the
    /// pair id the two rows share (<c>ExternalTransfer.AccountTransferId</c>), not by either row's own id.</summary>
    public Task<MutationResultDto> EditAccountTransferAsync(Guid id, Guid pairId, EditAccountTransferRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/account-transfers/{pairId}", req, ct);

    /// <summary>Remove both halves of an account-to-account transfer: the outflow here and the deposit it created there.</summary>
    public Task<MutationResultDto> RemoveAccountTransferAsync(Guid id, Guid pairId, Guid destinationAccountId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete,
            $"/accounts/{id}/account-transfers/{pairId}?destinationAccountId={destinationAccountId}", null, ct);

    public Task<MutationResultDto> RemoveCategoryAsync(Guid id, Guid categoryId, Guid? moveTo = null, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete,
            $"/accounts/{id}/categories/{categoryId}" + (moveTo is { } t ? $"?moveTo={t}" : ""), null, ct);
    public Task<MutationResultDto> CreateFundAsync(Guid id, CreateFundRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/funds", req, ct);
    public Task<MutationResultDto> EditFundAsync(Guid id, Guid fundId, EditFundRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/funds/{fundId}", req, ct);
    public Task<MutationResultDto> SetFundArchivedAsync(Guid id, Guid fundId, bool archived, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/funds/{fundId}/archived", new SetArchivedRequest(archived), ct);
    public Task<MutationResultDto> SetFundOpeningBalanceAsync(Guid id, Guid fundId, decimal amount, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/funds/{fundId}/opening-balance", new SetFundOpeningBalanceRequest(amount), ct);
    public Task<MutationResultDto> RemoveFundAsync(Guid id, Guid fundId, Guid? moveOpeningBalancesTo = null, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete,
            $"/accounts/{id}/funds/{fundId}{(moveOpeningBalancesTo is { } m ? $"?moveOpeningBalancesTo={m}" : "")}", null, ct);
    public Task<MutationResultDto> CreateContributionCategoryAsync(Guid id, CreateContributionCategoryRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/contribution-categories", req, ct);
    public Task<MutationResultDto> EditContributionCategoryAsync(Guid id, Guid catId, EditContributionCategoryRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/contribution-categories/{catId}", req, ct);
    public Task<MutationResultDto> RemoveContributionCategoryAsync(Guid id, Guid catId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete, $"/accounts/{id}/contribution-categories/{catId}", null, ct);

    // Tags (flat cross-cutting labels for expenses)
    public Task<MutationResultDto> CreateTagAsync(Guid id, CreateTagRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/tags", req, ct);
    public Task<MutationResultDto> EditTagAsync(Guid id, Guid tagId, EditTagRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/tags/{tagId}", req, ct);
    public Task<MutationResultDto> SetTagArchivedAsync(Guid id, Guid tagId, bool archived, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/tags/{tagId}/archived", new SetArchivedRequest(archived), ct);
    public Task<MutationResultDto> RemoveTagAsync(Guid id, Guid tagId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete, $"/accounts/{id}/tags/{tagId}", null, ct);

    // Trips
    public Task<MutationResultDto> CreateTripAsync(Guid id, CreateTripRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/trips", req, ct);
    public Task<MutationResultDto> EditTripAsync(Guid id, Guid tripId, EditTripRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/trips/{tripId}", req, ct);
    public Task<MutationResultDto> RemoveTripAsync(Guid id, Guid tripId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete, $"/accounts/{id}/trips/{tripId}", null, ct);
    public Task<MutationResultDto> StartTripAsync(Guid id, Guid tripId, bool started, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/trips/{tripId}/started", new StartTripRequest(started), ct);
    public Task<MutationResultDto> FinishTripAsync(Guid id, Guid tripId, bool finished, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/trips/{tripId}/finished", new FinishTripRequest(finished), ct);
    public Task<MutationResultDto> UseTripSavingsAsync(Guid id, Guid tripId, UseTripSavingsRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/trips/{tripId}/use-savings", req, ct);
    public Task<MutationResultDto> SetExpenseTripAsync(Guid id, Guid expenseId, Guid? tripId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/expenses/{expenseId}/trip", new SetExpenseTripRequest(tripId), ct);
    public Task<MutationResultDto> SeedTripTagsAsync(Guid id, SeedTripTagsRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/trip-tags", req, ct);

    // Recurring items
    public Task<MutationResultDto> AddRecurringAsync(Guid id, AddRecurringRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/recurring", req, ct);
    public Task<MutationResultDto> UpdateRecurringAsync(Guid id, Guid recurringId, UpdateRecurringRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/recurring/{recurringId}", req, ct);
    public Task<MutationResultDto> SetRecurringActiveAsync(Guid id, Guid recurringId, bool active, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/recurring/{recurringId}/active", new SetActiveRequest(active), ct);
    public Task<MutationResultDto> RemoveRecurringAsync(Guid id, Guid recurringId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete, $"/accounts/{id}/recurring/{recurringId}", null, ct);
    public Task<MutationResultDto> ConfirmRecurringAsync(Guid id, Guid recurringId, decimal actualAmount, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/recurring/{recurringId}/confirm", new ConfirmRecurringRequest(actualAmount), ct);
    public Task<MutationResultDto> SkipRecurringAsync(Guid id, Guid recurringId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/recurring/{recurringId}/skip", null, ct);
    public Task<MutationResultDto> UnskipRecurringAsync(Guid id, Guid recurringId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/recurring/{recurringId}/unskip", null, ct);

    // Budgets + reallocation
    public Task<MutationResultDto> SetBudgetAsync(Guid id, Guid categoryId, SetBudgetRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/budgets/{categoryId}", req, ct);
    public Task<MutationResultDto> RemoveBudgetAsync(Guid id, Guid categoryId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete, $"/accounts/{id}/budgets/{categoryId}", null, ct);
    public Task<MutationResultDto> ReallocateToSavingsAsync(Guid id, ReallocateToSavingsRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/reallocations/to-savings", req, ct);

    // Fund transfers (intra-account)
    public Task<MutationResultDto> TransferFundsAsync(Guid id, TransferFundsRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/fund-transfers", req, ct);
    public Task<MutationResultDto> EditFundTransferAsync(Guid id, Guid transferId, EditFundTransferRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/fund-transfers/{transferId}", req, ct);
    public Task<MutationResultDto> RemoveFundTransferAsync(Guid id, Guid transferId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete, $"/accounts/{id}/fund-transfers/{transferId}", null, ct);

    // Period lifecycle
    public Task<MutationResultDto> StartNextPeriodAsync(Guid id, StartNextPeriodRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/periods/start-next", req, ct);
    public Task<MutationResultDto> ReschedulePeriodAsync(Guid id, int index, ReschedulePeriodRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Put, $"/accounts/{id}/periods/{index}/schedule", req, ct);
    public Task<MutationResultDto> RemoveLatestPeriodAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete, $"/accounts/{id}/periods/latest", null, ct);

    // Statement import
    public Task<ImportResultDto> ImportTransactionsAsync(Guid id, ImportTransactionsRequest req, CancellationToken ct = default) =>
        SendAsync<ImportResultDto>(HttpMethod.Post, $"/accounts/{id}/import", req, ct);

    // Settlement / cross-account (two-account writes)
    public Task<MutationResultDto> TransferToAccountAsync(Guid id, TransferToAccountRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/transfers-out", req, ct);
    public Task<MutationResultDto> SettleExpenseAsync(Guid id, Guid expenseId, SettleExpenseRequest req, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Post, $"/accounts/{id}/expenses/{expenseId}/settle", req, ct);
    public Task<MutationResultDto> UnsettleExpenseAsync(Guid id, Guid expenseId, Guid destinationAccountId, CancellationToken ct = default) =>
        SendAsync<MutationResultDto>(HttpMethod.Delete, $"/accounts/{id}/expenses/{expenseId}/settle?destinationAccountId={destinationAccountId}", null, ct);

    // --- Path-B thin reads: the computed Home figures (docs/MOBILE.md) ----
    // These endpoints existed since Sessions 37/42 but were never wired to a client — the thin Home is their first use.
    // Path-B thin period navigation: ?period={index} views a past period; omitted = the current one.
    private static string PeriodQuery(int? periodIndex) => periodIndex is { } i ? $"?period={i}" : "";

    public Task<PeriodsViewDto> GetPeriodsAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<PeriodsViewDto>(HttpMethod.Get, $"/accounts/{id}/periods", null, ct);

    public Task<AccountOverviewDto> GetOverviewAsync(Guid id, int? periodIndex = null, CancellationToken ct = default) =>
        SendAsync<AccountOverviewDto>(HttpMethod.Get, $"/accounts/{id}/overview{PeriodQuery(periodIndex)}", null, ct);
    public Task<TargetsDto> GetTargetsAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<TargetsDto>(HttpMethod.Get, $"/accounts/{id}/targets", null, ct);
    public Task<MilestonesDto> GetMilestonesAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<MilestonesDto>(HttpMethod.Get, $"/accounts/{id}/milestones", null, ct);
    public Task<InsightsDto> GetInsightsAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<InsightsDto>(HttpMethod.Get, $"/accounts/{id}/insights", null, ct);

    /// <summary>The cash runway, or null when the server has no basis to project from (it answers 204, a real state
    /// distinct from zeroed figures).</summary>
    public async Task<RunwayDto?> GetRunwayAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(HttpMethod.Get, $"/accounts/{id}/runway", null, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<RunwayDto>(Json, ct);
    }

    // --- Path-B thin Spending slice (docs/MOBILE.md) ----------------------
    // The thin client renders Spending from these DTOs directly (no domain). The delta methods hit the SAME expense
    // routes as the thick client's command methods above — the server response is a superset, so both coexist.
    public Task<SpendingViewDto> GetSpendingAsync(Guid id, int? periodIndex = null, CancellationToken ct = default) =>
        SendAsync<SpendingViewDto>(HttpMethod.Get, $"/accounts/{id}/spending{PeriodQuery(periodIndex)}", null, ct);
    public Task<ExpenseMutationDto> AddExpenseDeltaAsync(Guid id, AddExpenseRequest req, CancellationToken ct = default) =>
        SendAsync<ExpenseMutationDto>(HttpMethod.Post, $"/accounts/{id}/expenses", req, ct);
    public Task<ExpenseMutationDto> EditExpenseDeltaAsync(Guid id, Guid expenseId, EditExpenseRequest req, CancellationToken ct = default) =>
        SendAsync<ExpenseMutationDto>(HttpMethod.Put, $"/accounts/{id}/expenses/{expenseId}", req, ct);
    public Task<ExpenseMutationDto> RemoveExpenseDeltaAsync(Guid id, Guid expenseId, CancellationToken ct = default) =>
        SendAsync<ExpenseMutationDto>(HttpMethod.Delete, $"/accounts/{id}/expenses/{expenseId}", null, ct);

    // --- Path-B thin Budgets slice ----------------------------------------
    public Task<BudgetsViewDto> GetBudgetsAsync(Guid id, int? periodIndex = null, CancellationToken ct = default) =>
        SendAsync<BudgetsViewDto>(HttpMethod.Get, $"/accounts/{id}/budgets{PeriodQuery(periodIndex)}", null, ct);
    public Task<BudgetMutationDto> SetBudgetDeltaAsync(Guid id, Guid categoryId, SetBudgetRequest req, CancellationToken ct = default) =>
        SendAsync<BudgetMutationDto>(HttpMethod.Put, $"/accounts/{id}/budgets/{categoryId}", req, ct);
    public Task<BudgetMutationDto> RemoveBudgetDeltaAsync(Guid id, Guid categoryId, CancellationToken ct = default) =>
        SendAsync<BudgetMutationDto>(HttpMethod.Delete, $"/accounts/{id}/budgets/{categoryId}", null, ct);

    // --- Path-B thin Recurring slice --------------------------------------
    public Task<RecurringViewDto> GetRecurringAsync(Guid id, int? periodIndex = null, CancellationToken ct = default) =>
        SendAsync<RecurringViewDto>(HttpMethod.Get, $"/accounts/{id}/recurring{PeriodQuery(periodIndex)}", null, ct);
    public Task<RecurringMutationDto> ConfirmRecurringDeltaAsync(Guid id, Guid recurringId, decimal actualAmount, CancellationToken ct = default) =>
        SendAsync<RecurringMutationDto>(HttpMethod.Post, $"/accounts/{id}/recurring/{recurringId}/confirm", new ConfirmRecurringRequest(actualAmount), ct);
    public Task<RecurringMutationDto> SkipRecurringDeltaAsync(Guid id, Guid recurringId, CancellationToken ct = default) =>
        SendAsync<RecurringMutationDto>(HttpMethod.Post, $"/accounts/{id}/recurring/{recurringId}/skip", null, ct);

    // --- Path-B thin Income slice -----------------------------------------
    public Task<IncomeViewDto> GetIncomeAsync(Guid id, int? periodIndex = null, CancellationToken ct = default) =>
        SendAsync<IncomeViewDto>(HttpMethod.Get, $"/accounts/{id}/income{PeriodQuery(periodIndex)}", null, ct);
    public Task<DepositMutationDto> AddDepositDeltaAsync(Guid id, AddDepositRequest req, CancellationToken ct = default) =>
        SendAsync<DepositMutationDto>(HttpMethod.Post, $"/accounts/{id}/deposits", req, ct);

    // --- Path-B thin Goals/Savings slice ----------------------------------
    public Task<SavingsViewDto> GetSavingsAsync(Guid id, int? periodIndex = null, CancellationToken ct = default) =>
        SendAsync<SavingsViewDto>(HttpMethod.Get, $"/accounts/{id}/savings{PeriodQuery(periodIndex)}", null, ct);
    public Task<SavingsMutationDto> AddSavingDepositDeltaAsync(Guid id, AddSavingDepositRequest req, CancellationToken ct = default) =>
        SendAsync<SavingsMutationDto>(HttpMethod.Post, $"/accounts/{id}/savings/deposits", req, ct);

    // --- Path-B thin Wallets slice ----------------------------------------
    public Task<WalletsViewDto> GetWalletsAsync(Guid id, int? periodIndex = null, CancellationToken ct = default) =>
        SendAsync<WalletsViewDto>(HttpMethod.Get, $"/accounts/{id}/wallets{PeriodQuery(periodIndex)}", null, ct);
    public Task<FundMutationDto> AddFundDeltaAsync(Guid id, CreateFundRequest req, CancellationToken ct = default) =>
        SendAsync<FundMutationDto>(HttpMethod.Post, $"/accounts/{id}/funds", req, ct);
    public Task<FundMutationDto> TransferFundsDeltaAsync(Guid id, TransferFundsRequest req, CancellationToken ct = default) =>
        SendAsync<FundMutationDto>(HttpMethod.Post, $"/accounts/{id}/fund-transfers", req, ct);

    /// <summary>Download the account as an .xlsx (one sheet per period). Returns the bytes + suggested file name.</summary>
    public async Task<(byte[] Bytes, string FileName)> ExportAccountAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(HttpMethod.Get, $"/accounts/{id}/export", null, ct);
        await EnsureSuccessAsync(response, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? "account.xlsx";
        return (bytes, fileName);
    }

    // --- Bank sync (Open Banking) -----------------------------------------
    public Task<BankSyncStatusDto> GetBankStatusAsync(Guid accountId, CancellationToken ct = default) =>
        SendAsync<BankSyncStatusDto>(HttpMethod.Get, $"/accounts/{accountId}/bank/status", null, ct);
    public Task<List<BankInstitutionDto>> GetBankInstitutionsAsync(Guid accountId, string country, CancellationToken ct = default) =>
        SendAsync<List<BankInstitutionDto>>(HttpMethod.Get, $"/accounts/{accountId}/bank/institutions?country={Uri.EscapeDataString(country)}", null, ct);
    public Task<StartBankLinkResponse> StartBankLinkAsync(Guid accountId, StartBankLinkRequest req, CancellationToken ct = default) =>
        SendAsync<StartBankLinkResponse>(HttpMethod.Post, $"/accounts/{accountId}/bank/link", req, ct);
    public Task SyncBankAsync(Guid accountId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/accounts/{accountId}/bank/sync", null, ct);
    public Task<List<PendingBankTransactionDto>> GetPendingBankTransactionsAsync(Guid accountId, CancellationToken ct = default) =>
        SendAsync<List<PendingBankTransactionDto>>(HttpMethod.Get, $"/accounts/{accountId}/bank/pending", null, ct);
    public Task<List<BankAccountDto>> GetBankAccountsAsync(Guid accountId, CancellationToken ct = default) =>
        SendAsync<List<BankAccountDto>>(HttpMethod.Get, $"/accounts/{accountId}/bank/accounts", null, ct);
    public Task<BankBalanceAtDto> GetBankBalanceAtAsync(Guid accountId, DateOnly date, CancellationToken ct = default) =>
        SendAsync<BankBalanceAtDto>(HttpMethod.Get, $"/accounts/{accountId}/bank/balance-at?date={date:yyyy-MM-dd}", null, ct);

    public Task SelectBankAccountAsync(Guid accountId, string bankAccountRef, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"/accounts/{accountId}/bank/account", new SelectBankAccountRequest(bankAccountRef), ct);
    public Task AckBankTransactionAsync(Guid accountId, string externalId, bool confirmed, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/accounts/{accountId}/bank/ack", new BankTransactionAck(externalId, confirmed), ct);
    public Task DisconnectBankAsync(Guid accountId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/accounts/{accountId}/bank/connection", null, ct);
    public Task ResetBankRangeAsync(Guid accountId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/accounts/{accountId}/bank/reset?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", null, ct);
    public Task SetBankFundAsync(Guid accountId, Guid? fundId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"/accounts/{accountId}/bank/fund", new SetBankFundRequest(fundId), ct);
    public Task<List<BankMappingDto>> GetBankMappingsAsync(Guid accountId, CancellationToken ct = default) =>
        SendAsync<List<BankMappingDto>>(HttpMethod.Get, $"/accounts/{accountId}/bank/mappings", null, ct);
    public Task SetBankMappingAsync(Guid accountId, string description, string kind, Guid targetId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"/accounts/{accountId}/bank/mappings", new SetBankMappingRequest(description, kind, targetId), ct);
    public Task RemoveBankMappingAsync(Guid accountId, string description, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/accounts/{accountId}/bank/mappings?description={Uri.EscapeDataString(description)}", null, ct);

    // --- Invitations ------------------------------------------------------
    public Task<List<InvitationDto>> GetPendingInvitationsAsync(CancellationToken ct = default) =>
        SendAsync<List<InvitationDto>>(HttpMethod.Get, "/invitations/pending", null, ct);
    public Task InviteAsync(Guid accountId, string username, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/accounts/{accountId}/invitations", new CreateInvitationRequest(username), ct);
    public async Task<Guid> AcceptInvitationAsync(Guid invitationId, CancellationToken ct = default) =>
        (await SendAsync<AcceptResult>(HttpMethod.Post, $"/invitations/{invitationId}/accept", null, ct)).AccountId;
    public Task DeclineInvitationAsync(Guid invitationId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/invitations/{invitationId}/decline", null, ct);

    private record AcceptResult(Guid AccountId);

    // --- Plumbing ---------------------------------------------------------
    // Auth endpoints must never trigger the 401→refresh→retry path (a failing /auth/refresh would recurse).
    private static bool IsAuthPath(string path) =>
        path is "/auth/refresh" or "/auth/login" or "/auth/register" or "/auth/logout"
             or "/auth/exchange" or "/auth/2fa";

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, body, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, body, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>Send with the bearer token attached. On a 401 for a normal (non-auth) request, ask the
    /// <see cref="OnUnauthorized"/> handler to refresh the token, then retry the request exactly once.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var tokenUsed = Token;
        var response = await SendOnceAsync(method, path, body, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized
            && OnUnauthorized is not null && !IsAuthPath(path) && !string.IsNullOrEmpty(tokenUsed))
        {
            response.Dispose();
            await OnUnauthorized(tokenUsed);              // refreshes Token, or clears it if the session is dead
            response = await SendOnceAsync(method, path, body, ct);  // retry with whatever token we now hold
        }
        return response;
    }

    /// <summary>Bodies at or above this compress before going on the wire; smaller ones aren't worth the gzip
    /// header and the CPU. The account snapshot is the reason this exists — it's ~260KB of JSON re-sent on every
    /// mutation, and measured against a real account the upload was ~70% of the time a save took.</summary>
    private const int CompressBodiesOver = 8 * 1024;

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = BuildContent(body);
        if (!string.IsNullOrEmpty(Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return await http.SendAsync(request, ct);
    }

    /// <summary>JSON body, gzipped when it's big enough to pay for itself. The server decompresses transparently
    /// (UseRequestDecompression), so this is invisible to every endpoint. Built fresh per attempt because content
    /// streams can't be re-sent — the 401-refresh path sends twice.</summary>
    private static HttpContent BuildContent(object body)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        if (json.Length < CompressBodiesOver)
            return new ByteArrayContent(json) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };

        using var ms = new MemoryStream();
        // Fastest, not Optimal: this runs on the UI thread in WASM, and JSON still compresses ~8x at this level.
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(json, 0, json.Length);

        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var message = response.ReasonPhrase ?? "Request failed.";
        string? feature = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, ct);
            if (!string.IsNullOrWhiteSpace(error?.Error)) message = error!.Error;
            feature = error?.Feature;
        }
        catch { /* non-JSON error body — keep the reason phrase */ }

        // A Pro-gated 402 raises the upgrade prompt (same UX as a local gate) and throws a typed exception so the
        // caller aborts cleanly. Falls back to the plain path if the server didn't name a feature.
        if (response.StatusCode == HttpStatusCode.PaymentRequired && !string.IsNullOrWhiteSpace(feature))
        {
            PaymentRequired?.Invoke(feature!);
            throw new PaymentRequiredApiException(feature!, message);
        }

        throw new ApiException(response.StatusCode, message);
    }

    private record ErrorBody(string Error, string? Feature);
}
