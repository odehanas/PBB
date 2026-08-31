using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace GovBudget.Controllers
{
    [Authorize]
    public class GuidesController : Controller
    {
        private readonly IWebHostEnvironment _env;

        public GuidesController(IWebHostEnvironment env)
        {
            _env = env;
        }

        // Curated list of the prepared guide documents (stored under /docs).
        //
        // One complete guide per language replaced the previous five-document set (user
        // guide, user manual, administrator guide, submission workflow and what-if). They
        // overlapped heavily, drifted apart as the system changed, and left readers unsure
        // which one to trust. Everything they covered now lives in a single document per
        // language, so there is exactly one place to read and one place to update.
        private static readonly List<GuideDoc> Docs = new()
        {
            new GuideDoc(
                "Complete Guide",
                "Everything in one document: concepts, budget preparation, submission and approval, cost allocation, cost per hour, reports and administration.",
                "GovBudget_Guide_EN.html", "EN", "Complete"),
            new GuideDoc(
                "الدليل الشامل",
                "كل شيء في مستند واحد: المفاهيم، إعداد الموازنة، التقديم والاعتماد، توزيع التكاليف، تكلفة الساعة، التقارير والإدارة.",
                "GovBudget_Guide_AR.html", "AR", "Complete"),
            new GuideDoc(
                "Power BI Reporting Guide",
                "Technical reference for the combined-cost SQL views used in Power BI.",
                "PowerBI_Reporting_Guide.md", "EN", "Reference"),
        };

        [HttpGet]
        public IActionResult Index()
        {
            return View(Docs);
        }

        // Serves a file from the /docs folder. The catch-all route keeps relative
        // asset paths (e.g. images/01-login.png) resolving correctly from a guide page.
        [HttpGet("Guides/Content/{*path}")]
        public IActionResult Content(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return NotFound();
            }

            var docsRoot = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "docs"));
            var requested = Path.GetFullPath(Path.Combine(docsRoot, path));

            // Guard against path traversal outside the docs folder.
            if (!requested.StartsWith(docsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(requested, docsRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            // Only the curated documents and their images are served. The docs folder also
            // carries internal material (security review, schema scripts) that must never be
            // reachable from a browser.
            var relative = requested[docsRoot.Length..]
                .TrimStart(Path.DirectorySeparatorChar)
                .Replace('\\', '/');

            var isCurated = Docs.Any(d => string.Equals(d.File, relative, StringComparison.OrdinalIgnoreCase));
            var isAsset = relative.StartsWith("images/", StringComparison.OrdinalIgnoreCase);

            if (!isCurated && !isAsset)
            {
                return NotFound();
            }

            if (!System.IO.File.Exists(requested))
            {
                return NotFound();
            }

            var provider = new FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(requested, out var contentType))
            {
                // Fall back to text for markdown/plain files so they display in-browser.
                var ext = Path.GetExtension(requested).ToLowerInvariant();
                contentType = ext is ".md" or ".txt" or ".sql" ? "text/plain; charset=utf-8" : "application/octet-stream";
            }

            var bytes = System.IO.File.ReadAllBytes(requested);
            return File(bytes, contentType);
        }

        public record GuideDoc(string Title, string Description, string File, string Lang, string Category);
    }
}
