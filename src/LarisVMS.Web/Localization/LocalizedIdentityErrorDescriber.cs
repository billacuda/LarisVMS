using Microsoft.AspNetCore.Identity;
using LarisVMS.Core.Localization;

namespace LarisVMS.Web.Localization;

/// <summary>
/// ASP.NET Core Identity's error messages (password rules, duplicate names, and so on) in the
/// display language. Admins see them when creating users and roles or setting passwords; the codes
/// are unchanged, only the descriptions are translated.
/// </summary>
public sealed class LocalizedIdentityErrorDescriber : IdentityErrorDescriber
{
    private static IdentityError Error(string code, string key, object? args = null) =>
        new() { Code = code, Description = Loc.T(key, args) };

    private static IdentityError PluralError(string code, string key, int count) =>
        new() { Code = code, Description = Loc.Plural(key, count) };

    public override IdentityError DefaultError() => Error(nameof(DefaultError), "identity.error.default");
    public override IdentityError ConcurrencyFailure() => Error(nameof(ConcurrencyFailure), "identity.error.concurrencyFailure");
    public override IdentityError PasswordMismatch() => Error(nameof(PasswordMismatch), "identity.error.passwordMismatch");
    public override IdentityError InvalidToken() => Error(nameof(InvalidToken), "identity.error.invalidToken");
    public override IdentityError LoginAlreadyAssociated() => Error(nameof(LoginAlreadyAssociated), "identity.error.loginAlreadyAssociated");
    public override IdentityError InvalidUserName(string? userName) => Error(nameof(InvalidUserName), "identity.error.invalidUserName", new { userName });
    public override IdentityError InvalidEmail(string? email) => Error(nameof(InvalidEmail), "identity.error.invalidEmail", new { email });
    public override IdentityError DuplicateUserName(string userName) => Error(nameof(DuplicateUserName), "identity.error.duplicateUserName", new { userName });
    public override IdentityError DuplicateEmail(string email) => Error(nameof(DuplicateEmail), "identity.error.duplicateEmail", new { email });
    public override IdentityError InvalidRoleName(string? role) => Error(nameof(InvalidRoleName), "identity.error.invalidRoleName", new { role });
    public override IdentityError DuplicateRoleName(string role) => Error(nameof(DuplicateRoleName), "identity.error.duplicateRoleName", new { role });
    public override IdentityError UserAlreadyHasPassword() => Error(nameof(UserAlreadyHasPassword), "identity.error.userAlreadyHasPassword");
    public override IdentityError UserLockoutNotEnabled() => Error(nameof(UserLockoutNotEnabled), "identity.error.userLockoutNotEnabled");
    public override IdentityError UserAlreadyInRole(string role) => Error(nameof(UserAlreadyInRole), "identity.error.userAlreadyInRole", new { role });
    public override IdentityError UserNotInRole(string role) => Error(nameof(UserNotInRole), "identity.error.userNotInRole", new { role });
    public override IdentityError PasswordTooShort(int length) => PluralError(nameof(PasswordTooShort), "identity.error.passwordTooShort", length);
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => PluralError(nameof(PasswordRequiresUniqueChars), "identity.error.passwordRequiresUniqueChars", uniqueChars);
    public override IdentityError PasswordRequiresNonAlphanumeric() => Error(nameof(PasswordRequiresNonAlphanumeric), "identity.error.passwordRequiresNonAlphanumeric");
    public override IdentityError PasswordRequiresDigit() => Error(nameof(PasswordRequiresDigit), "identity.error.passwordRequiresDigit");
    public override IdentityError PasswordRequiresLower() => Error(nameof(PasswordRequiresLower), "identity.error.passwordRequiresLower");
    public override IdentityError PasswordRequiresUpper() => Error(nameof(PasswordRequiresUpper), "identity.error.passwordRequiresUpper");
}
