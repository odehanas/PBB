using System;
using GovBudget.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GovBudget.Services
{
    // Idempotent schema guard for allocation scenarios: adds the optional ScenarioName label to
    // core.AllocationRuns so management can name a run ("Headcount basis") and compare scenarios
    // in Management Review. Safe to run on every startup and on a database that already has it.
    public static class AllocationScenarioUpgrade
    {
        private const string AddScenarioName =
            "IF OBJECT_ID('core.AllocationRuns','U') IS NOT NULL AND COL_LENGTH('core.AllocationRuns','ScenarioName') IS NULL " +
            "ALTER TABLE core.AllocationRuns ADD ScenarioName NVARCHAR(120) NULL;";

        public static void Run(GovBudgetContext db, ILogger logger)
        {
            try
            {
                db.Database.ExecuteSqlRaw(AddScenarioName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Allocation scenario schema statement failed: {Sql}", AddScenarioName);
            }
        }
    }
}
