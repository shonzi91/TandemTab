using FinApp.Contracts;
using FinApp.Domain.Common;
using FinApp.Domain.Users;
using FinApp.Persistence;
using FinApp.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Auth;

/// <summary>Registers and authenticates users, returning an access token + refresh token on success.</summary>
public sealed class AuthService(
    FinAppDbContext db, IPasswordHasher hasher, JwtTokenService tokens,
    RefreshTokenService refreshTokens, AuthCodeService authCodes,
    EmailVerificationService emailVerification, IEmailSender email, TwoFactorService twoFactor,
    SessionPolicy sessionPolicy, PasswordResetService passwordReset, SignupService signups,
    BetaPolicy beta)
{
    private const int MinPasswordLength = 8;

    /// <summary>Send a password-reset link to whoever owns <paramref name="identifier"/> (a username or email).
    /// Deliberately silent about whether an account exists: it always succeeds from the caller's view, and only a
    /// real, matching account receives a mail — otherwise the endpoint would leak which emails are registered.</summary>
    public async Task SendPasswordResetEmailAsync(string identifier, string linkBaseUrl, CancellationToken ct = default)
    {
        var id = (identifier ?? "").Trim();
        if (id.Length == 0) return;
        var lowered = id.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Email == lowered || u.Username.ToLower() == lowered, ct);
        if (user is null) return;   // no such account — say nothing, do nothing (no enumeration)

        var token = await passwordReset.IssueTokenAsync(user.Id, ct);
        var link = $"{linkBaseUrl.TrimEnd('/')}/?resetToken={Uri.EscapeDataString(token)}";
        var html =
            $"<p>Someone asked to reset the password for your TandemTab account.</p>" +
            $"<p>Click the link below to choose a new password. It expires in an hour and can be used once:</p>" +
            $"<p><a href=\"{link}\">Reset my password</a></p>" +
            $"<p>If you didn't ask for this, you can safely ignore this email — your password won't change.</p>";
        var text = $"Someone asked to reset the password for your TandemTab account.\n\n" +
                   $"Choose a new password (link expires in an hour, single use):\n{link}\n\n" +
                   "If you didn't ask for this, you can ignore this email — your password won't change.";
        await email.SendAsync(user.Email, "Reset your TandemTab password", html, text, ct);
    }

    /// <summary>Redeem a reset token and set a new password. Throws on an invalid/expired token or a too-short
    /// password. On success every existing session is revoked, so a compromised session can't outlive the reset.</summary>
    public async Task ResetPasswordAsync(string token, string newPassword, CancellationToken ct = default)
    {
        if ((newPassword ?? "").Length < MinPasswordLength)
            throw new BadRequestException($"Password must be at least {MinPasswordLength} characters.");
        var userId = await passwordReset.ConsumeAsync(token, ct)
            ?? throw new BadRequestException("This reset link is invalid or has expired. Request a new one.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new BadRequestException("This reset link is invalid or has expired. Request a new one.");
        user.SetPasswordHash(hasher.Hash(newPassword!));
        await db.SaveChangesAsync(ct);
        await refreshTokens.RevokeAllForUserAsync(userId, ct);   // sign out everywhere after a reset
    }

    /// <summary>Issue a verification token and email the confirmation link. Best-effort: failures don't block sign-up.</summary>
    public async Task SendVerificationEmailAsync(Guid userId, string toEmail, string linkBaseUrl, CancellationToken ct = default)
    {
        var token = await emailVerification.IssueTokenAsync(userId, toEmail, ct);
        var link = $"{linkBaseUrl.TrimEnd('/')}/auth/verify-email?token={Uri.EscapeDataString(token)}";
        var html =
            $"<p>Welcome to TandemTab!</p><p>Please confirm your email address by clicking the link below:</p>" +
            $"<p><a href=\"{link}\">Confirm my email</a></p>" +
            $"<p>If you didn't create a TandemTab account, you can ignore this email.</p>";
        var text = $"Welcome to TandemTab!\n\nConfirm your email address:\n{link}\n\n" +
                   "If you didn't create a TandemTab account, you can ignore this email.";
        await email.SendAsync(toEmail, "Confirm your TandemTab email", html, text, ct);
    }

    /// <summary>Verify an email from the token in the confirmation link. Returns true on success.</summary>
    public Task<bool> VerifyEmailAsync(string token, CancellationToken ct = default) =>
        emailVerification.VerifyAsync(token, ct);

    /// <summary>Email the freshly-generated 2FA recovery codes to the user — but only if their address is verified
    /// (never send security material to an unconfirmed inbox). Best-effort: failures are logged, not thrown, so
    /// enabling 2FA still succeeds. Gives the user a durable copy since the codes are shown only once on screen.</summary>
    public async Task EmailRecoveryCodesAsync(Guid userId, string toEmail, string[] recoveryCodes, CancellationToken ct = default)
    {
        if (!await emailVerification.IsVerifiedAsync(userId, toEmail, ct)) return;
        var list = string.Join("", recoveryCodes.Select(c => $"<li><code>{c}</code></li>"));
        var html =
            "<p>You just turned on two-factor authentication for TandemTab. Keep these one-time recovery codes " +
            "somewhere safe — each works once if you lose access to your authenticator app:</p>" +
            $"<ul>{list}</ul><p>If you didn't enable two-factor authentication, secure your account immediately.</p>";
        var text = "You just turned on two-factor authentication for TandemTab. Keep these one-time recovery codes " +
                   "somewhere safe — each works once if you lose access to your authenticator app:\n\n" +
                   string.Join("\n", recoveryCodes) +
                   "\n\nIf you didn't enable two-factor authentication, secure your account immediately.";
        await email.SendAsync(toEmail, "Your TandemTab two-factor recovery codes", html, text, ct);
    }

    /// <summary>Issue an access token plus a fresh refresh token for a user.</summary>
    private async Task<AuthResponse> IssueAsync(User user, CancellationToken ct)
    {
        var response = tokens.Issue(user);
        var days = await sessionPolicy.RefreshDaysForAsync(user.Id, ct);
        var refresh = await refreshTokens.IssueAsync(user.Id, days, ct);
        return response with { RefreshToken = refresh };
    }

    /// <summary>Exchange a valid refresh token for a new access token (rotating the refresh token).</summary>
    public async Task<AuthResponse> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var rotated = await refreshTokens.ValidateAndRotateAsync(refreshToken, sessionPolicy.RefreshDaysForAsync, ct)
            ?? throw new UnauthorizedException("Your session has expired. Please sign in again.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == rotated.UserId, ct)
            ?? throw new UnauthorizedException("Your session has expired. Please sign in again.");
        return tokens.Issue(user) with { RefreshToken = rotated.NewToken };
    }

    /// <summary>Revoke a refresh token on sign-out (best-effort; unknown tokens are ignored).</summary>
    public Task LogoutAsync(string refreshToken, CancellationToken ct = default) =>
        refreshTokens.RevokeAsync(refreshToken, ct);

    /// <summary>Redeem a one-time external-sign-in code (from the OAuth redirect). If the account has 2FA enabled,
    /// no tokens are issued yet: returns a challenge with a fresh ticket to complete via <c>/auth/2fa</c> (same
    /// second-factor flow as a password login). Otherwise issues an access + refresh token.</summary>
    public async Task<LoginResponse> ExchangeAsync(string code, CancellationToken ct = default)
    {
        var userId = await authCodes.RedeemAsync(code, ct)
            ?? throw new UnauthorizedException("This sign-in link is invalid or has expired. Please try again.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedException("This sign-in link is invalid or has expired. Please try again.");

        // External sign-ins are now 2FA-gated too: the provider proved who the user is, but a second factor
        // still stands between the OAuth redirect and real session tokens.
        if (await twoFactor.IsEnabledAsync(user.Id, ct))
        {
            var ticket = await authCodes.IssueAsync(user.Id, ct);
            return new LoginResponse(TwoFactorRequired: true, TwoFactorTicket: ticket);
        }

        return new LoginResponse(TwoFactorRequired: false, Auth: await IssueAsync(user, ct));
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var username = (request.Username ?? "").Trim();
        var email = (request.Email ?? "").Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(username))
            throw new BadRequestException("Username is required.");
        if ((request.Password ?? "").Length < MinPasswordLength)
            throw new BadRequestException($"Password must be at least {MinPasswordLength} characters.");

        if (await db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower(), ct))
            throw new ConflictException("That username is already taken.");
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ConflictException("That email is already registered.");

        // Beta capacity. Checked AFTER the cheap validity checks so a full beta never masks a plain typo, and
        // BEFORE the user row is written so a refused sign-up leaves nothing behind. Our own test addresses skip
        // the cap entirely — see BetaPolicy.
        var isTestAccount = beta.IsTestEmail(email);
        if (!isTestAccount && beta.Enabled
            && beta.IsFull(await signups.BetaSeatsTakenAsync(beta.CountFrom, ct)))
        {
            throw new ConflictException(
                "The free beta is full right now. Write to admin@tandemtab.com and we'll save you a spot in the next round.");
        }

        User user;
        try
        {
            user = new User(username, email, hasher.Hash(request.Password!));
        }
        catch (ArgumentException ex)
        {
            throw new BadRequestException(ex.CleanMessage());
        }

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        // Stamped as "test" for our own addresses so they never occupy a capped seat (and, usefully, aren't
        // grandfathered to Pro — a test account is how you see the Free tier).
        await signups.RecordAsync(user.Id, beta.CohortFor(email), ct);   // OPEN-BETA B4 — capture who joined, when
        return await IssueAsync(user, ct);
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var identifier = (request.UsernameOrEmail ?? "").Trim();
        var identifierLower = identifier.ToLowerInvariant();

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Username.ToLower() == identifierLower || u.Email == identifierLower, ct);

        if (user is null || !hasher.Verify(request.Password ?? "", user.PasswordHash))
            throw new UnauthorizedException("Invalid username or password.");

        // If 2FA is on, don't hand out tokens yet: return a short-lived ticket proving the password step passed.
        if (await twoFactor.IsEnabledAsync(user.Id, ct))
        {
            var ticket = await authCodes.IssueAsync(user.Id, ct);
            return new LoginResponse(TwoFactorRequired: true, TwoFactorTicket: ticket);
        }

        return new LoginResponse(TwoFactorRequired: false, Auth: await IssueAsync(user, ct));
    }

    /// <summary>Complete a 2FA-gated login: validate the code against the ticket, then issue tokens.</summary>
    public async Task<AuthResponse> TwoFactorLoginAsync(string ticket, string code, CancellationToken ct = default)
    {
        // Resolve (don't consume) the ticket so a mistyped code can be retried within its short TTL.
        var userId = await authCodes.ResolveAsync(ticket, ct)
            ?? throw new UnauthorizedException("This sign-in attempt has expired. Please sign in again.");
        if (!await twoFactor.VerifyAsync(userId, code, ct))
            throw new UnauthorizedException("That code isn't right. Try again.");

        // Correct code — now consume the ticket (single-use) and issue tokens.
        await authCodes.RedeemAsync(ticket, ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedException("This sign-in attempt has expired. Please sign in again.");
        return await IssueAsync(user, ct);
    }

    /// <summary>Find an existing user by email or create one for an external (Google/Facebook) sign-in, returning the
    /// user id. External users get a random password hash (they sign in via the provider, not a password). The caller
    /// hands the user a one-time code (see <see cref="AuthCodeService"/>) rather than a token in the URL.</summary>
    public async Task<Guid> FindOrCreateExternalUserAsync(string email, string? displayName, CancellationToken ct = default)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            throw new BadRequestException("The sign-in provider didn't return an email address.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            var baseName = MakeUsername(displayName, email);
            var username = baseName;
            var n = 1;
            while (await db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower(), ct))
                username = $"{baseName}{++n}";

            // The cap applies to the OAuth path too — otherwise "sign in with Google" would be an open side door
            // around it. Existing users fall outside this branch entirely, so a full beta never locks out
            // somebody who already has an account.
            if (!beta.IsTestEmail(email) && beta.Enabled
                && beta.IsFull(await signups.BetaSeatsTakenAsync(beta.CountFrom, ct)))
            {
                throw new ConflictException(
                    "The free beta is full right now. Write to admin@tandemtab.com and we'll save you a spot in the next round.");
            }

            var randomSecret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            user = new User(username, email, hasher.Hash(randomSecret));
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            await signups.RecordAsync(user.Id, beta.CohortFor(email), ct);   // OPEN-BETA B4 — external sign-ups too
        }
        // The provider already confirmed this address, so treat it as verified.
        await emailVerification.MarkVerifiedAsync(user.Id, user.Email, ct);
        return user.Id;
    }

    private static string MakeUsername(string? displayName, string email)
    {
        var src = !string.IsNullOrWhiteSpace(displayName) ? displayName! : email.Split('@')[0];
        var clean = new string(src.Trim().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' or '.').ToArray()).Trim();
        if (clean.Length == 0) clean = "user";
        return clean.Length > 30 ? clean[..30] : clean;
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedException("Not signed in.");
        if (!hasher.Verify(request.CurrentPassword ?? "", user.PasswordHash))
            throw new BadRequestException("Current password is incorrect.");
        if ((request.NewPassword ?? "").Length < MinPasswordLength)
            throw new BadRequestException($"Password must be at least {MinPasswordLength} characters.");

        user.SetPasswordHash(hasher.Hash(request.NewPassword!));
        await db.SaveChangesAsync(ct);
    }
}
