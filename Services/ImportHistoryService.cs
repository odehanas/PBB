using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using GovBudget.Models;
using Microsoft.EntityFrameworkCore;

namespace GovBudget.Services
{
    public static class ImportTypes
    {
        public const string MidYear = "MIDYEAR";
        public const string Historical = "HISTORICAL";
        public const string BudgetLines = "BUDGETLINES";
        public const string Items = "ITEMS";
    }

    public static class ImportTables
    {
        public const string MidYear = "MidYearGlActualForecasts";
        public const string Historical = "HistoricalGlActuals";
        public const string BudgetLines = "BudgetLines";
        public const string Items = "Items";
    }

    /// <summary>
    /// Collects what one upload does to the data, so it can be written to the import log in the
    /// same transaction. Snapshots of updated/deleted records must be taken BEFORE they change.
    /// </summary>
    public sealed class ImportRecorder
    {
        internal readonly List<(string Table, string Type, Func<long> Id, string? Before, Func<string?>? After)> Changes = new();

        public int Created => Changes.Count(c => c.Type == "CREATED");
        public int Updated => Changes.Count(c => c.Type == "UPDATED");
        public int Deleted => Changes.Count(c => c.Type == "DELETED");

        /// <summary>
        /// A new record; its id is read after SaveChanges has assigned it. Pass an "after" snapshot
        /// (here and in AddUpdated) for tables with no "last updated" column, so an undo can tell
        /// whether the record was changed by hand since the upload.
        /// </summary>
        public void AddCreated(string table, Func<long> idAfterSave, Func<string?>? after = null) => Changes.Add((table, "CREATED", idAfterSave, null, after));

        public void AddUpdated(string table, long id, object before, Func<string?>? after = null) => Changes.Add((table, "UPDATED", () => id, ImportSnapshot.Take(before), after));

        public void AddDeleted(string table, long id, object before) => Changes.Add((table, "DELETED", () => id, ImportSnapshot.Take(before), null));
    }

    /// <summary>Serialises the scalar columns of an entity (and a budget line's document).</summary>
    public static class ImportSnapshot
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

        private static bool IsScalar(Type t)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            return u.IsPrimitive || u.IsEnum || u == typeof(string) || u == typeof(decimal)
                   || u == typeof(DateTime) || u == typeof(DateTimeOffset) || u == typeof(Guid)
                   || u == typeof(byte[]);
        }

        private static IEnumerable<PropertyInfo> ScalarProps(Type t) =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
             .Where(p => p.CanRead && p.CanWrite && IsScalar(p.PropertyType));

        public static string Take(object entity)
        {
            var dict = ScalarProps(entity.GetType()).ToDictionary(p => p.Name, p => p.GetValue(entity));
            if (entity is BudgetLines bl && bl.BudgetLineDocuments != null)
            {
                dict["__Document"] = ScalarProps(typeof(BudgetLineDocuments))
                    .ToDictionary(p => p.Name, p => p.GetValue(bl.BudgetLineDocuments));
            }
            return JsonSerializer.Serialize(dict, Json);
        }

        /// <summary>Copies the snapshot onto an entity, skipping the listed key columns.</summary>
        public static void Restore(string json, object target, params string[] skip)
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var p in ScalarProps(target.GetType()))
            {
                if (skip.Contains(p.Name)) continue;
                if (!doc.RootElement.TryGetProperty(p.Name, out var el)) continue;
                p.SetValue(target, el.Deserialize(p.PropertyType, Json));
            }
        }

        public static BudgetLineDocuments? RestoreDocument(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("__Document", out var d) || d.ValueKind != JsonValueKind.Object) return null;
            var document = new BudgetLineDocuments();
            Restore(d.GetRawText(), document, nameof(BudgetLineDocuments.BudgetLineId));
            return document;
        }
    }

    public sealed record UndoResult(bool Ok, string Message);

    /// <summary>
    /// Writes the import log and undoes uploads. Every upload is recorded inside the same
    /// database transaction as the data change, so the log can never disagree with the data.
    /// </summary>
    public sealed class ImportHistoryService
    {
        private readonly GovBudgetContext _db;

        public ImportHistoryService(GovBudgetContext db)
        {
            _db = db;
        }

        /// <summary>
        /// Runs <paramref name="work"/> inside a transaction under the context's execution
        /// strategy (the app uses SQL retry-on-failure, which requires this pattern). The work
        /// delegate must load everything it needs itself, because a retry starts from a clean
        /// change tracker.
        /// </summary>
        public async Task<T> RunInTransactionAsync<T>(Func<Task<T>> work)
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync();
                var result = await work();
                await tx.CommitAsync();
                return result;
            });
        }

        /// <summary>
        /// Call after the data changes have been saved (so new ids exist), inside the same
        /// transaction. Writes the batch header and one change row per touched record.
        /// </summary>
        public async Task<ImportBatches> SaveBatchAsync(ImportRecorder rec, string importType,
            int? budgetYear, int? entityId, string? scope, string? sourceFile, string? user)
        {
            var batch = new ImportBatches
            {
                ImportType = importType,
                BudgetYear = budgetYear,
                EntityId = entityId,
                Scope = scope,
                SourceFile = sourceFile,
                ImportedBy = user,
                // App clock, taken after the data was written: anything changed later than this
                // was changed by someone else, which is what the undo safety check relies on.
                ImportedAt = DateTime.UtcNow,
                RowsCreated = rec.Created,
                RowsUpdated = rec.Updated,
                RowsDeleted = rec.Deleted,
                Status = "APPLIED"
            };
            _db.ImportBatches.Add(batch);
            await _db.SaveChangesAsync();

            foreach (var c in rec.Changes)
            {
                _db.ImportBatchChanges.Add(new ImportBatchChanges
                {
                    ImportBatchId = batch.ImportBatchId,
                    TableName = c.Table,
                    ChangeType = c.Type,
                    RecordId = c.Id(),
                    BeforeJson = c.Before,
                    AfterJson = c.After?.Invoke()
                });
            }
            await _db.SaveChangesAsync();
            return batch;
        }

        /// <summary>
        /// Why a batch cannot be undone, or null when it can. Checked again inside the undo
        /// transaction.
        /// </summary>
        public async Task<string?> WhyNotUndoableAsync(long batchId)
        {
            var batch = await _db.ImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.ImportBatchId == batchId);
            if (batch == null) return "Upload not found.";
            if (batch.Status != "APPLIED") return "This upload has already been undone.";

            var changes = await _db.ImportBatchChanges.AsNoTracking()
                .Where(c => c.ImportBatchId == batchId)
                .Select(c => new { c.TableName, c.RecordId, c.ChangeType })
                .ToListAsync();
            if (changes.Count == 0) return "This upload has no recorded changes to undo.";

            // 1) A later upload that touched any of the same records must be undone first.
            var tables = changes.Select(c => c.TableName).Distinct().ToList();
            var ids = changes.Select(c => c.RecordId).Distinct().ToList();
            var later = await (
                from c in _db.ImportBatchChanges.AsNoTracking()
                join b in _db.ImportBatches.AsNoTracking() on c.ImportBatchId equals b.ImportBatchId
                where b.ImportBatchId > batchId && b.Status == "APPLIED"
                      && tables.Contains(c.TableName) && ids.Contains(c.RecordId)
                select b.ImportBatchId).Distinct().ToListAsync();
            if (later.Count > 0)
            {
                return $"A later upload (#{string.Join(", #", later)}) changed the same records. Undo that upload first.";
            }

            // 2) Records edited by hand after the upload would silently lose those edits.
            // BudgetLines.UpdatedAt is datetime2(0), so SQL Server can round the upload's own
            // stamp up past ImportedAt; only a time beyond that rounding counts as a later edit.
            var editedAfter = batch.ImportedAt.AddSeconds(1);
            var touched = changes.Where(c => c.ChangeType != "DELETED").ToList();
            var midIds = touched.Where(c => c.TableName == ImportTables.MidYear).Select(c => c.RecordId).ToList();
            if (midIds.Count > 0)
            {
                var edited = await _db.MidYearGlActualForecasts.AsNoTracking()
                    .CountAsync(x => midIds.Contains(x.MidYearId) && x.ForecastUpdatedAt > editedAfter);
                if (edited > 0) return $"{edited} mid-year row(s) had their H2 forecast edited on screen after this upload. Undo would discard those edits.";
            }

            var lineIds = touched.Where(c => c.TableName == ImportTables.BudgetLines).Select(c => c.RecordId).ToList();
            if (lineIds.Count > 0)
            {
                var edited = await _db.BudgetLines.AsNoTracking()
                    .CountAsync(x => lineIds.Contains(x.BudgetLineId) && x.UpdatedAt > editedAfter);
                if (edited > 0) return $"{edited} budget line(s) were edited after this upload. Undo would discard those edits.";
            }

            // Items have no "last updated" column, so compare them with what the upload wrote.
            var itemAfter = await _db.ImportBatchChanges.AsNoTracking()
                .Where(c => c.ImportBatchId == batchId && c.TableName == ImportTables.Items && c.AfterJson != null)
                .Select(c => new { c.RecordId, c.AfterJson })
                .ToListAsync();
            if (itemAfter.Count > 0)
            {
                var itemIds = itemAfter.Select(c => (int)c.RecordId).ToList();
                var currentItems = await _db.Items.AsNoTracking().Where(x => itemIds.Contains(x.ItemId)).ToListAsync();
                var afterById = itemAfter.ToDictionary(c => c.RecordId, c => c.AfterJson);
                var editedItems = currentItems.Where(x => ImportSnapshot.Take(x) != afterById[x.ItemId]).ToList();
                if (editedItems.Count > 0)
                {
                    return $"{editedItems.Count} item(s) were edited after this upload ({string.Join(", ", editedItems.Take(5).Select(x => x.ItemCode))}). Undo would discard those edits.";
                }
            }

            // 3) Budget lines in a submitted / locked budget must not change.
            if (batch.ImportType == ImportTypes.BudgetLines)
            {
                var deletedJson = await _db.ImportBatchChanges.AsNoTracking()
                    .Where(c => c.ImportBatchId == batchId && c.TableName == ImportTables.BudgetLines && c.ChangeType == "DELETED")
                    .Select(c => c.BeforeJson!).ToListAsync();
                var scopes = await _db.BudgetLines.AsNoTracking()
                    .Where(x => lineIds.Contains(x.BudgetLineId))
                    .Select(x => new { x.BudgetYear, x.EntityId, x.DepartmentId, x.CategoryId })
                    .ToListAsync();
                foreach (var json in deletedJson)
                {
                    var l = new BudgetLines();
                    ImportSnapshot.Restore(json, l);
                    scopes.Add(new { l.BudgetYear, l.EntityId, l.DepartmentId, l.CategoryId });
                }
                foreach (var s in scopes.Distinct())
                {
                    var status = await _db.BudgetSubmissions.AsNoTracking()
                        .Where(x => x.BudgetYear == s.BudgetYear && x.EntityId == s.EntityId
                                    && x.DepartmentId == s.DepartmentId && x.CategoryId == s.CategoryId)
                        .OrderByDescending(x => x.VersionNo)
                        .Select(x => x.Status)
                        .FirstOrDefaultAsync();
                    if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "Draft", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"The budget for {s.BudgetYear} is {status}; it must be returned to Draft before this upload can be undone.";
                    }
                }
            }

            // 4) New items that budget lines already use cannot be removed.
            var newItemIds = changes.Where(c => c.TableName == ImportTables.Items && c.ChangeType == "CREATED")
                .Select(c => (int)c.RecordId).ToList();
            if (newItemIds.Count > 0)
            {
                var used = await _db.BudgetLines.AsNoTracking().CountAsync(x => newItemIds.Contains(x.ItemId));
                if (used > 0) return $"{used} budget line(s) already use items created by this upload, so the items cannot be removed.";
            }

            return null;
        }

        public async Task<UndoResult> UndoAsync(long batchId, string user)
        {
            var reason = await WhyNotUndoableAsync(batchId);
            if (reason != null) return new UndoResult(false, reason);

            return await RunInTransactionAsync(async () =>
            {
                var batch = await _db.ImportBatches.FirstAsync(b => b.ImportBatchId == batchId);
                if (batch.Status != "APPLIED") return new UndoResult(false, "This upload has already been undone.");

                var changes = await _db.ImportBatchChanges.AsNoTracking()
                    .Where(c => c.ImportBatchId == batchId)
                    .OrderByDescending(c => c.ImportChangeId)
                    .ToListAsync();

                int removed = 0, restored = 0, reinserted = 0, missing = 0;
                var putBack = new List<(long OldId, BudgetLines Line)>();

                foreach (var c in changes)
                {
                    switch (c.TableName)
                    {
                        case ImportTables.MidYear:
                        {
                            var row = await _db.MidYearGlActualForecasts.FirstOrDefaultAsync(x => x.MidYearId == c.RecordId);
                            if (c.ChangeType == "CREATED") { if (row != null) { _db.MidYearGlActualForecasts.Remove(row); removed++; } else missing++; }
                            else if (c.ChangeType == "UPDATED") { if (row != null) { ImportSnapshot.Restore(c.BeforeJson!, row, nameof(row.MidYearId)); restored++; } else missing++; }
                            break;
                        }
                        case ImportTables.Historical:
                        {
                            var row = await _db.HistoricalGlActuals.FirstOrDefaultAsync(x => x.HistoricalActualId == c.RecordId);
                            if (c.ChangeType == "CREATED") { if (row != null) { _db.HistoricalGlActuals.Remove(row); removed++; } else missing++; }
                            else if (c.ChangeType == "UPDATED") { if (row != null) { ImportSnapshot.Restore(c.BeforeJson!, row, nameof(row.HistoricalActualId)); restored++; } else missing++; }
                            break;
                        }
                        case ImportTables.Items:
                        {
                            var row = await _db.Items.FirstOrDefaultAsync(x => x.ItemId == (int)c.RecordId);
                            if (c.ChangeType == "CREATED") { if (row != null) { _db.Items.Remove(row); removed++; } else missing++; }
                            else if (c.ChangeType == "UPDATED") { if (row != null) { ImportSnapshot.Restore(c.BeforeJson!, row, nameof(row.ItemId)); restored++; } else missing++; }
                            break;
                        }
                        case ImportTables.BudgetLines:
                        {
                            if (c.ChangeType == "CREATED")
                            {
                                await _db.BudgetLineDocuments.Where(d => d.BudgetLineId == c.RecordId).ExecuteDeleteAsync();
                                var n = await _db.BudgetLines.Where(x => x.BudgetLineId == c.RecordId).ExecuteDeleteAsync();
                                if (n > 0) removed++; else missing++;
                            }
                            else if (c.ChangeType == "DELETED")
                            {
                                var line = new BudgetLines();
                                ImportSnapshot.Restore(c.BeforeJson!, line, nameof(line.BudgetLineId));
                                var document = ImportSnapshot.RestoreDocument(c.BeforeJson!);
                                if (document != null) line.BudgetLineDocuments = document;
                                _db.BudgetLines.Add(line);
                                putBack.Add((c.RecordId, line));
                                reinserted++;
                            }
                            break;
                        }
                    }
                }

                batch.Status = "UNDONE";
                batch.UndoneAt = DateTime.UtcNow;
                batch.UndoneBy = user;

                _db.AuditLogs.Add(new AuditLogs
                {
                    UserName = user,
                    Action = "UPLOAD_UNDO",
                    EntityName = "ImportBatches",
                    RecordId = batch.ImportBatchId.ToString(),
                    Timestamp = DateTime.UtcNow,
                    Details = $"Undid {batch.ImportType} upload #{batch.ImportBatchId} ({batch.SourceFile}): removed {removed}, restored {restored}, re-inserted {reinserted}."
                });

                await _db.SaveChangesAsync();

                // Lines put back get new ids. Point other uploads' log rows at them, otherwise
                // undoing the upload that originally created these lines would find nothing.
                if (putBack.Count > 0)
                {
                    var newIdByOld = putBack.ToDictionary(p => p.OldId, p => p.Line.BudgetLineId);
                    var oldIds = newIdByOld.Keys.ToList();
                    var otherLogRows = await _db.ImportBatchChanges
                        .Where(x => x.ImportBatchId != batchId && x.TableName == ImportTables.BudgetLines && oldIds.Contains(x.RecordId))
                        .ToListAsync();
                    foreach (var logRow in otherLogRows)
                    {
                        logRow.RecordId = newIdByOld[logRow.RecordId];
                    }
                    await _db.SaveChangesAsync();
                }

                var msg = $"Upload #{batch.ImportBatchId} undone: {removed} added row(s) removed, {restored} changed row(s) restored, {reinserted} replaced row(s) put back.";
                if (missing > 0) msg += $" {missing} row(s) no longer existed and were skipped.";
                return new UndoResult(true, msg);
            });
        }
    }
}
