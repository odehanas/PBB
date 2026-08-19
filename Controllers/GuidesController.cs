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
        private static readonly List<GuideDoc> Docs = new()
        {
            new GuideDoc("User Guide", "Step-by-step guide for budget preparers: context, entry, submission and reports.", "GovBudget_User_Guide.html", "EN", "End User"),
            new GuideDoc("دليل المستخدم", "دليل خطوة بخطوة لمُعِدّي الموازنة: السياق، الإدخال، التقديم والتقارير.", "GovBudget_User_Guide_AR.html", "AR", "End User"),
            new GuideDoc("User Manual", "Comprehensive reference manual covering every screen and workflow.", "GovBudget_User_Manual.html", "EN", "End User"),
            new GuideDoc("Submission Workflow Guide", "How budgets move from draft to submitted, returned and approved.", "GovBudget_Submission_Workflow_Guide.html", "EN", "End User"),
            new GuideDoc("دليل سير الاعتماد", "كيفية انتقال الموازنة من مسودة إلى مُقدّمة ومُعادة ومُعتمدة.", "GovBudget_Submission_Workflow_Guide_AR.html", "AR", "End User"),
            new GuideDoc("What-If Scenarios Guide", "Building and comparing what-if budget scenarios (Arabic & English).", "GovBudget_WhatIf_Guide_AR_EN.html", "AR / EN", "End User"),
            new GuideDoc("Administrator Guide", "Setup and administration: entities, users, master data, programmes and allocation.", "GovBudget_Admin_Guide.html", "EN", "Administrator"),
            new GuideDoc("دليل المسؤول", "الإعداد والإدارة: الجهات والمستخدمون والبيانات المرجعية والبرامج والتوزيع.", "GovBudget_Admin_Guide_AR.html", "AR", "Administrator"),
            new GuideDoc("Power BI Reporting Guide", "Technical reference for the combined-cost SQL views used in Power BI.", "PowerBI_Reporting_Guide.md", "EN", "Reference"),
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
