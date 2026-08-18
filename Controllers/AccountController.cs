using System.Security.Claims;
using System.Threading.Tasks;
using GovBudget.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Controllers
{
    public class AccountController : Controller
    {
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
        public async Task<IActionResult> Login(string userName, string password, string? returnUrl = null)
        {
            var u = await _db.AppUsers.FirstOrDefaultAsync(x => x.UserName == userName && x.IsActive);
            if (u == null || u.Password != password) // NOTE: demo only; replace with hashing later
            {
                ModelState.AddModelError("", "Invalid username or password.");
                return View();
            }

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
                new Claim("DepartmentId", departmentId?.ToString() ?? "")
            };
            var id = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id));

            // Audit Log
            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = u.UserName,
                Action = "LOGIN",
                Timestamp = DateTime.UtcNow,
                Details = "User logged in successfully."
            });
            await _db.SaveChangesAsync();

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return LocalRedirect(returnUrl);
            }

            return RedirectToAction("Index", "Home");
        }

        [HttpGet]
        public IActionResult ForgotPassword()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
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
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
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

            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
            {
                ModelState.AddModelError("", "Password must be at least 6 characters.");
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

            user.Password = newPassword;

            req.Status = "Completed";
            req.TokenUsedAt = DateTime.UtcNow;
            req.CompletedAt = DateTime.UtcNow;

            _db.AuditLogs.Add(new AuditLogs
            {
                UserName = user.UserName,
                Action = "UPDATE",
                EntityName = "AppUsers",
                RecordId = user.UserId.ToString(),
                Timestamp = DateTime.UtcNow,
                Details = "Password reset completed via reset link."
            });

            await _db.SaveChangesAsync();

            TempData["Success"] = "Your password has been updated. Please sign in.";
            return RedirectToAction(nameof(Login));
        }

        private async Task<PasswordResetRequests?> FindValidResetRequestAsync(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;

            var now = DateTime.UtcNow;
            return await _db.PasswordResetRequests.FirstOrDefaultAsync(r =>
                r.Token == token
                && r.TokenUsedAt == null
                && (r.TokenExpiresAt == null || r.TokenExpiresAt > now));
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
