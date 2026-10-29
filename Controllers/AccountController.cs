using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using GovBudget.Models;
using GovBudget.Services;
using GovBudget.Utils;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    public class AccountController : Controller
    {
        // Account lockout: five wrong passwords park the account for fifteen minutes.
        private const int MaxFailedAttempts = 5;
        private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

        // Reset links are credentials: short-lived and single-use.
        public static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromMinutes(60);

        private readonly GovBudgetContext _db;
        public AccountController(GovBudgetContext db) { _db = db; }

        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("login")]
        public async Task<IActionResult> Login(string userName, string password, string? returnUrl = null)
        {
            userName = (userName ?? "").Trim();
            ViewBag.ReturnUrl = returnUrl;

            var now = DateTime.UtcNow;
            var u = await _db.AppUsers.FirstOrDefaultAsync(x => x.UserName == userName);

            // Same message for every failure reason so the form cannot be used to work out
            // which usernames exist or which accounts are disabled.
            const string genericError = "Invalid username or password.";

            if (u == null || !u.IsActive)
            {
                await LogLoginFailureAsync(userName, u == null ? "Unknown username." : "Account is inactive.");
                ModelState.AddModelError("", genericError);
                return View();
            }

            if (u.LockoutEndUtc.HasValue && u.LockoutEndUtc.Value > now)
            {
                var minutes = Math.Max(1, (int)Math.Ceiling((u.LockoutEndUtc.Value - now).TotalMinutes));
                await LogLoginFailureAsync(userName, "Attempt while locked out.");
                ModelState.AddModelError("", $"This account is temporarily locked after too many failed attempts. Try again in {minutes} minute(s) or ask an administrator for a reset link.");
                return View();
            }

            var verified = false;

            if (PasswordHasher.IsHashed(u.PasswordHash))
            {
                verified = PasswordHasher.Verify(password, u.PasswordHash, out var needsRehash);
                if (verified && needsRehash)
                {
                    u.PasswordHash = PasswordHasher.Hash(password);
                }
            }
            else if (!string.IsNullOrEmpty(u.Password))
            {
                // Legacy clear-text row that the startup upgrade has not reached yet:
                // accept the existing password once, then store it hashed and wipe the
                // clear-text value. The user notices nothing.
                verified = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(u.Password),
                    System.Text.Encoding.UTF8.GetBytes(password ?? ""));

                if (verified)
                {
                    u.PasswordHash = PasswordHasher.Hash(password!);
                    u.Password = null;
                    u.PasswordUpdatedAt ??= now;
                }
            }

            if (!verified)
            {
                u.FailedLoginCount += 1;

                var lockedNow = u.FailedLoginCount >= MaxFailedAttempts;
                if (lockedNow)
                {
                    u.LockoutEndUtc = now.Add(LockoutDuration);
                    u.FailedLoginCount = 0;
                }

                await LogLoginFailureAsync(userName, lockedNow
                    ? $"Wrong password - account locked for {LockoutDuration.TotalMinutes:0} minutes."
                    : "Wrong password.");

                ModelState.AddModelError("", lockedNow
                    ? $"Too many failed attempts. This account is locked for {LockoutDuration.TotalMinutes:0} minutes."
                    : genericError);

                return View();
            }

            u.FailedLoginCount = 0;
            u.LockoutEndUtc = null;
            u.LastLoginAt = now;
            if (string.IsNullOrEmpty(u.SecurityStamp)) u.SecurityStamp = PasswordHasher.NewSecurityStamp();

            await SignInUserAsync(u);

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = u.UserName,
                Action = "LOGIN",
                EntityName = "AppUsers",
                RecordId = u.UserId.ToString(),
                Timestamp = now,
                Details = $"Successful sign-in from {ClientAddress()}."
            });
            await _db.SaveChangesAsync();

            if (u.MustChangePassword)
            {
                return RedirectToAction(nameof(ChangePassword), new { forced = 1 });
            }

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return LocalRedirect(returnUrl);
            }

            return RedirectToAction("Index", "Home");
        }

        // Issues the auth cookie for a verified user. Also used after a password change so
        // the cookie carries the new security stamp.
        private async Task SignInUserAsync(AppUsers u)
        {
            var role = (u.Role ?? "").Trim().ToUpperInvariant();

            int? entityId = null;
            int? departmentId = null;
            if (role != "SYSADMIN")
            {
                entityId = u.EntityId;

                if (role != "ADMIN")
                {
                    departmentId = u.DepartmentId;
                }

                if (!entityId.HasValue && u.DepartmentId.HasValue)
                {
                    entityId = await _db.Departments
                        .AsNoTracking()
                        .Where(d => d.DepartmentId == u.DepartmentId.Value)
                        .Select(d => (int?)d.EntityId)
                        .FirstOrDefaultAsync();
                }
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.Name, u.UserName),
                new Claim(ClaimTypes.Role, role),
                new Claim("EntityId", entityId?.ToString() ?? ""),
                new Claim("DepartmentId", departmentId?.ToString() ?? ""),
                new Claim(CookieSecurityValidator.SecurityStampClaim, u.SecurityStamp ?? ""),
                new Claim(CookieSecurityValidator.MustChangePasswordClaim, u.MustChangePassword ? "1" : "0")
            };

            var id = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id));
        }

        private string ClientAddress()
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var forwarded = Request.Headers["X-Forwarded-For"].ToString();
            return string.IsNullOrWhiteSpace(forwarded) ? ip : $"{ip} (via {forwarded})";
        }

        // Failed sign-ins are recorded so repeated attempts are visible in the audit log.
        private async Task LogLoginFailureAsync(string attemptedUserName, string reason)
        {
            var name = string.IsNullOrWhiteSpace(attemptedUserName) ? "(blank)" : attemptedUserName;
            if (name.Length > 100) name = name[..100];

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = name,
                Action = "LOGIN_FAILED",
                EntityName = "AppUsers",
                RecordId = "",
                Timestamp = DateTime.UtcNow,
                Details = $"{reason} Source {ClientAddress()}."
            });

            await _db.SaveChangesAsync();
        }

        // GET: Account/ChangePassword
        [HttpGet]
        [Authorize]
        public IActionResult ChangePassword(int? forced = null)
        {
            ViewBag.Forced = forced == 1
                || string.Equals(User.FindFirst(CookieSecurityValidator.MustChangePasswordClaim)?.Value, "1", StringComparison.Ordinal);
            ViewBag.PolicySummary = PasswordPolicy.Summary;
            return View();
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword, string confirmPassword)
        {
            ViewBag.Forced = string.Equals(User.FindFirst(CookieSecurityValidator.MustChangePasswordClaim)?.Value, "1", StringComparison.Ordinal);
            ViewBag.PolicySummary = PasswordPolicy.Summary;

            var userName = User.Identity?.Name ?? "";
            var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.UserName == userName && u.IsActive);
            if (user == null)
            {
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return RedirectToAction(nameof(Login));
            }

            var currentOk = PasswordHasher.IsHashed(user.PasswordHash)
                ? PasswordHasher.Verify(currentPassword, user.PasswordHash, out _)
                : !string.IsNullOrEmpty(user.Password) && user.Password == currentPassword;

            if (!currentOk)
            {
                ModelState.AddModelError("", "Your current password is not correct.");
                return View();
            }

            if (!PasswordPolicy.Validate(newPassword, user.UserName, out var policyError))
            {
                ModelState.AddModelError("", policyError);
                return View();
            }

            if (newPassword != confirmPassword)
            {
                ModelState.AddModelError("", "The new passwords do not match.");
                return View();
            }

            if (PasswordHasher.IsHashed(user.PasswordHash)
                && PasswordHasher.Verify(newPassword, user.PasswordHash, out _))
            {
                ModelState.AddModelError("", "The new password must be different from the current one.");
                return View();
            }

            ApplyNewPassword(user, newPassword);

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = user.UserName,
                Action = "UPDATE",
                EntityName = "AppUsers",
                RecordId = user.UserId.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Password changed by the user from {ClientAddress()}."
            });

            await _db.SaveChangesAsync();

            // Re-issue the cookie so it carries the new security stamp and drops the
            // must-change flag.
            await SignInUserAsync(user);

            TempData["Success"] = "Your password has been changed.";
            return RedirectToAction("Index", "Home");
        }

        // Central place where a new password is stored: always hashed, clear-text wiped,
        // lockout cleared and the security stamp rotated so other sessions are dropped.
        private void ApplyNewPassword(AppUsers user, string newPassword)
        {
            user.PasswordHash = PasswordHasher.Hash(newPassword);
            user.Password = null;
            user.PasswordUpdatedAt = DateTime.UtcNow;
            user.MustChangePassword = false;
            user.FailedLoginCount = 0;
            user.LockoutEndUtc = null;
            user.SecurityStamp = PasswordHasher.NewSecurityStamp();
        }

        [HttpGet]
        public IActionResult ForgotPassword()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("login")]
        public async Task<IActionResult> ForgotPassword(string userName, string? contactInfo, string? note)
        {
            userName = (userName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(userName))
            {
                ModelState.AddModelError("", "Please enter your username.");
                return View();
            }

            // Only create a request for a real, active user, but always show the same
            // generic confirmation so usernames cannot be enumerated from this page.
            var user = await _db.AppUsers
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.UserName == userName && u.IsActive);

            if (user != null)
            {
                int? entityId = user.EntityId;
                if (!entityId.HasValue && user.DepartmentId.HasValue)
                {
                    entityId = await _db.Departments
                        .AsNoTracking()
                        .Where(d => d.DepartmentId == user.DepartmentId.Value)
                        .Select(d => (int?)d.EntityId)
                        .FirstOrDefaultAsync();
                }

                _db.PasswordResetRequests.Add(new PasswordResetRequests
                {
                    UserName = user.UserName,
                    UserId = user.UserId,
                    EntityId = entityId,
                    ContactInfo = string.IsNullOrWhiteSpace(contactInfo) ? null : contactInfo.Trim(),
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                    Status = "Pending",
                    RequestSource = "Login",
                    RequestedAt = DateTime.UtcNow
                });

                _db.AuditLogs.Add(new AuditLogs
                {
                    UserName = user.UserName,
                    Action = "INSERT",
                    EntityName = "PasswordResetRequests",
                    RecordId = "",
                    Timestamp = DateTime.UtcNow,
                    Details = "User requested a password reset from the login page."
                });

                await _db.SaveChangesAsync();
            }

            ViewBag.Submitted = true;
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> ResetPassword(string? token)
        {
            var req = await FindValidResetRequestAsync(token);
            if (req == null)
            {
                ViewBag.Invalid = true;
                return View();
            }

            ViewBag.Token = token;
            ViewBag.UserName = req.UserName;
            ViewBag.PolicySummary = PasswordPolicy.Summary;
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [EnableRateLimiting("login")]
        public async Task<IActionResult> ResetPassword(string token, string newPassword, string confirmPassword)
        {
            var req = await FindValidResetRequestAsync(token);
            if (req == null)
            {
                ViewBag.Invalid = true;
                return View();
            }

            ViewBag.Token = token;
            ViewBag.UserName = req.UserName;

            ViewBag.PolicySummary = PasswordPolicy.Summary;

            if (!PasswordPolicy.Validate(newPassword, req.UserName, out var policyError))
            {
                ModelState.AddModelError("", policyError);
                return View();
            }

            if (newPassword != confirmPassword)
            {
                ModelState.AddModelError("", "Passwords do not match.");
                return View();
            }

            var user = req.UserId.HasValue
                ? await _db.AppUsers.FirstOrDefaultAsync(u => u.UserId == req.UserId.Value)
                : await _db.AppUsers.FirstOrDefaultAsync(u => u.UserName == req.UserName);

            if (user == null)
            {
                ViewBag.Invalid = true;
                return View();
            }

            ApplyNewPassword(user, newPassword);

            req.Status = "Completed";
            req.TokenUsedAt = DateTime.UtcNow;
            req.CompletedAt = DateTime.UtcNow;
            req.Token = null;
            req.TokenHash = null;      // single use: the link cannot be replayed

            // Any other outstanding link for the same user is burnt as well.
            var others = await _db.PasswordResetRequests
                .Where(r => r.ResetRequestId != req.ResetRequestId
                            && r.TokenUsedAt == null
                            && r.TokenHash != null
                            && (r.UserId == user.UserId || r.UserName == user.UserName))
                .ToListAsync();

            foreach (var other in others)
            {
                other.TokenHash = null;
                other.Token = null;
                other.TokenExpiresAt = DateTime.UtcNow.AddMinutes(-1);
                other.Status = "Rejected";
                other.AdminNote = "Superseded by a completed reset.";
            }

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = user.UserName,
                Action = "UPDATE",
                EntityName = "AppUsers",
                RecordId = user.UserId.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = $"Password reset completed via reset link from {ClientAddress()}. Other pending links invalidated: {others.Count}."
            });

            await _db.SaveChangesAsync();

            TempData["Success"] = "Your password has been updated. Please sign in.";
            return RedirectToAction(nameof(Login));
        }

        // A link is only accepted when its digest matches, it has not been used and it has a
        // real expiry that is still in the future. A row without an expiry is never valid.
        private async Task<PasswordResetRequests?> FindValidResetRequestAsync(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;

            var now = DateTime.UtcNow;
            var hash = PasswordHasher.HashToken(token);

            return await _db.PasswordResetRequests.FirstOrDefaultAsync(r =>
                r.TokenHash == hash
                && r.TokenUsedAt == null
                && r.TokenExpiresAt != null
                && r.TokenExpiresAt > now);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Login");
        }

        public IActionResult Denied() => View();
    }
}
