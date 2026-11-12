using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using LarisVMS.Core.Entities;

namespace LarisVMS.Web.Auth;

/// <summary>
/// Identity's sign-in manager, plus a sign-in for an account whose password was checked somewhere
/// else (Active Directory) that still asks for the second step when the account has two-factor
/// sign-in on. Identity's own <c>SignInAsync</c> skips it; the step itself is protected.
/// </summary>
public sealed class LarisSignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    /// <summary>Signs in, or returns <see cref="SignInResult.TwoFactorRequired"/> with the
    /// second-step cookie set, exactly as a password sign-in does.</summary>
    public Task<SignInResult> SignInCheckingTwoFactorAsync(ApplicationUser user, bool isPersistent) =>
        SignInOrTwoFactorAsync(user, isPersistent);
}
