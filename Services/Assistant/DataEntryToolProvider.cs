using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GovBudget.Models;
using GovBudget.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GovBudget.Services.Assistant
{
    /// <summary>
    /// Lets the assistant prepare data entry on any screen in <see cref="AssistantWriteCatalog"/>.
    /// It never writes: propose_change only builds the form a screen would post, and the user's
    /// browser submits it after the user confirms, through the screen's own endpoint.
    /// </summary>
    public sealed class DataEntryToolProvider : IAssistantToolProvider
    {
        private static readonly MethodInfo FindRecordsMethod =
            typeof(DataEntryToolProvider).GetMethod(nameof(FindRecordsAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static readonly MethodInfo EfPropertyMethod = typeof(EF).GetMethod(nameof(EF.Property))!;

        private readonly GovBudgetContext _db;
        private readonly AssistantWriteCatalog _catalog;
        private readonly IPermissionService _permissions;
        private readonly IHttpContextAccessor _http;
        private readonly LinkGenerator _links;
        private readonly AssistantProposalCollector _proposals;
        private readonly AssistantOptions _options;

        public DataEntryToolProvider(
            GovBudgetContext db,
            AssistantWriteCatalog catalog,
            IPermissionService permissions,
            IHttpContextAccessor http,
            LinkGenerator links,
            AssistantProposalCollector proposals,
            IOptions<AssistantOptions> options)
        {
            _db = db;
            _catalog = catalog;
            _permissions = permissions;
            _http = http;
            _links = links;
            _proposals = proposals;
            _options = options.Value;
        }

        public IEnumerable<AssistantToolDefinition> GetTools()
        {
            yield return new AssistantToolDefinition(
                "list_write_actions",
                "List the data-entry actions this user may perform on the application screens (add, edit, delete, submit, approve...).",
                """{"type":"object","properties":{},"additionalProperties":false}""",
                ListActionsAsync);

            yield return new AssistantToolDefinition(
                "describe_write_action",
                "Show the fields an action takes. With record_id, also returns the record's current values.",
                """
                {"type":"object","properties":{
                  "action":{"type":"string","description":"Action key from list_write_actions, e.g. BudgetLines.Entry."},
                  "record_id":{"type":"integer","description":"Optional id of the existing record to edit or delete."}
                },"required":["action"],"additionalProperties":false}
                """,
                DescribeAsync);

            yield return new AssistantToolDefinition(
                "find_records",
                "Find existing records an action works on (budget lines, KPIs, programs, allocations...) to get their ids and current values.",
                """
                {"type":"object","properties":{
                  "action":{"type":"string","description":"Action key whose record type to search."},
                  "search":{"type":"string","description":"Text to look for in the record's text fields."},
                  "top":{"type":"integer","description":"Maximum records (default 25)."}
                },"required":["action"],"additionalProperties":false}
                """,
                FindAsync);

            yield return new AssistantToolDefinition(
                "propose_change",
                "Prepare a change for the user to confirm. Nothing is saved until the user clicks Confirm on the card. "
                + "For edits give record_id; fields you leave out keep their current values.",
                """
                {"type":"object","properties":{
                  "action":{"type":"string","description":"Action key from list_write_actions."},
                  "record_id":{"type":"integer","description":"Id of the record to edit, delete or act on."},
                  "values":{"type":"object","description":"Field values by field name, as returned by describe_write_action."},
                  "summary":{"type":"string","description":"One sentence telling the user what will happen."}
                },"required":["action","values","summary"],"additionalProperties":false}
                """,
                ProposeAsync);
        }

        private async Task<string> ListActionsAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var actions = new List<object>();
            foreach (var a in _catalog.Actions.Values)
            {
                if (!await AllowedAsync(a)) continue;
                actions.Add(new
                {
                    action = a.Key,
                    kind = a.Kind.ToString().ToLowerInvariant(),
                    a.Description,
                    has_records = a.RecordType is not null
                });
            }
            return Json(new { actions });
        }

        private async Task<string> DescribeAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var (action, error) = await ResolveAsync(args);
            if (action is null) return Json(new { error });

            object? current = null;
            var recordId = Long(args, "record_id");
            if (recordId is > 0 && action.RecordType is not null)
            {
                var (record, recordError) = await LoadRecordAsync(action.RecordType, recordId.Value, user, ct);
                if (record is null) return Json(new { error = recordError });
                current = ScalarValues(record);
            }

            return Json(new
            {
                action = action.Key,
                kind = action.Kind.ToString().ToLowerInvariant(),
                action.Description,
                key_field = action.KeyField,
                fields = action.Fields.Select(f => new
                {
                    name = f.Name,
                    type = f.Type,
                    required = f.Required,
                    array = f.IsArray,
                    row_fields = f.Rows?.Select(r => new { name = r.Name, type = r.Type })
                }),
                current_values = current
            });
        }

        private async Task<string> FindAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var (action, error) = await ResolveAsync(args);
            if (action is null) return Json(new { error });
            if (action.RecordType is null) return Json(new { error = $"{action.Key} does not work on stored records." });

            var top = Math.Clamp((int)(Long(args, "top") ?? 25), 1, _options.MaxRows);
            var task = (Task<List<Dictionary<string, object?>>>)FindRecordsMethod
                .MakeGenericMethod(action.RecordType)
                .Invoke(this, new object?[] { Str(args, "search"), top, user, ct })!;
            var records = await task;

            return Json(new { action = action.Key, key = PrimaryKey(action.RecordType)?.Name, records });
        }

        private async Task<string> ProposeAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var (action, error) = await ResolveAsync(args);
            if (action is null) return Json(new { error });

            var values = args.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object
                ? v.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

            var unknown = values.Keys.Where(k => action.Fields.All(f => !f.Name.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0)
            {
                return Json(new { error = "Unknown fields.", unknown, valid_fields = action.Fields.Select(f => f.Name) });
            }

            // The record being edited, deleted or acted on, from record_id or the action's key field.
            object? record = null;
            var recordId = Long(args, "record_id");
            if (recordId is null && action.KeyField is not null && values.TryGetValue(action.KeyField, out var keyValue))
            {
                recordId = ToLong(keyValue);
            }
            if (recordId is > 0 && action.RecordType is not null)
            {
                var (loaded, recordError) = await LoadRecordAsync(action.RecordType, recordId.Value, user, ct);
                if (loaded is null) return Json(new { error = recordError });
                record = loaded;
            }

            var current = record is null
                ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object?>(ScalarValues(record), StringComparer.OrdinalIgnoreCase);

            // A budget line is saved under its category's code, which the line itself only holds as an id.
            if (record is BudgetLines line && !values.ContainsKey("currentCategory"))
            {
                current["currentCategory"] = await _db.Categories.Where(c => c.CategoryId == line.CategoryId)
                    .Select(c => c.CategoryCode).FirstOrDefaultAsync(ct);
            }
            if (action.KeyField is not null && recordId is > 0)
            {
                current[action.KeyField] = recordId.Value;
            }

            var form = new List<KeyValuePair<string, string>>();
            var changes = new List<AssistantProposalField>();
            var missing = new List<string>();

            foreach (var field in action.Fields)
            {
                var provided = values.TryGetValue(field.Name, out var value) && value.ValueKind != JsonValueKind.Null;

                if (field.Rows is not null)
                {
                    if (!provided || value.ValueKind != JsonValueKind.Array)
                    {
                        if (field.Required) missing.Add(field.Name);
                        continue;
                    }
                    var i = 0;
                    foreach (var row in value.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Object) continue;
                        foreach (var sub in field.Rows)
                        {
                            if (row.TryGetProperty(sub.Name, out var cell) && cell.ValueKind != JsonValueKind.Null)
                            {
                                var text = FormValue(cell, sub.Type);
                                form.Add(new($"{field.Name}[{i}].{sub.Name}", text));
                                changes.Add(new($"{field.Name} {i + 1} · {sub.Name}", text, null));
                            }
                        }
                        i++;
                    }
                    continue;
                }

                if (field.IsArray)
                {
                    if (!provided)
                    {
                        if (field.Required) missing.Add(field.Name);
                        continue;
                    }
                    var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : new List<JsonElement> { value };
                    foreach (var item in items)
                    {
                        form.Add(new(field.Name, FormValue(item, field.Type)));
                    }
                    changes.Add(new(field.Name, string.Join(", ", items.Select(x => FormValue(x, field.Type))), null));
                    continue;
                }

                current.TryGetValue(field.Name, out var stored);
                var previous = stored is null ? null : StoredValue(stored);

                if (provided)
                {
                    var text = FormValue(value, field.Type);
                    form.Add(new(field.Name, text));
                    if (previous != text) changes.Add(new(field.Name, text, record is null ? null : previous));
                }
                else if (previous is not null)
                {
                    form.Add(new(field.Name, previous));
                }
                else if (field.Required)
                {
                    missing.Add(field.Name);
                }
            }

            if (missing.Count > 0)
            {
                return Json(new { error = "Required fields are missing. Ask the user or look them up.", missing });
            }

            var http = _http.HttpContext!;
            var url = _links.GetPathByAction(http, action.Action, action.Controller);
            if (url is null) return Json(new { error = "That screen could not be resolved." });

            var proposal = new AssistantProposal(
                Guid.NewGuid().ToString("N")[..12],
                action.Key,
                action.Kind.ToString().ToLowerInvariant(),
                Title(action),
                Str(args, "summary") ?? action.Description,
                url,
                record is null ? null : RecordLabel(record, recordId!.Value),
                form,
                changes);
            _proposals.Proposals.Add(proposal);

            return Json(new
            {
                status = "awaiting_user_confirmation",
                proposal_id = proposal.Id,
                note = "Nothing is saved yet. The user sees a card and must click Confirm. Do not say it was saved."
            });
        }

        private async Task<(WriteAction? Action, string? Error)> ResolveAsync(JsonElement args)
        {
            var key = Str(args, "action");
            if (key is null || !_catalog.Actions.TryGetValue(key, out var action))
            {
                return (null, "Unknown action. Call list_write_actions for the valid keys.");
            }
            if (!await AllowedAsync(action))
            {
                return (null, "The user's role does not allow this action.");
            }
            return (action, null);
        }

        private async Task<bool> AllowedAsync(WriteAction action)
        {
            var user = _http.HttpContext?.User;
            return user is not null
                && await FormPermissionFilter.CanPostAsync(_permissions, user, action.Controller, action.Action);
        }

        private async Task<(object? Record, string? Error)> LoadRecordAsync(Type type, long id, AssistantUserContext user, CancellationToken ct)
        {
            var key = PrimaryKey(type);
            if (key is null) return (null, "This record type has no single key.");

            object? record;
            try
            {
                record = await _db.FindAsync(type, new[] { Convert.ChangeType(id, key.ClrType, CultureInfo.InvariantCulture) }, ct);
            }
            catch (OverflowException)
            {
                record = null;
            }

            if (record is null) return (null, $"No record with id {id}.");
            if (!InScope(record, user)) return (null, "That record is outside the user's entity or cost center.");

            // Detach so a later lookup in the same request cannot see half-merged values.
            _db.Entry(record).State = EntityState.Detached;
            return (record, null);
        }

        private async Task<List<Dictionary<string, object?>>> FindRecordsAsync<T>(
            string? search, int top, AssistantUserContext user, CancellationToken ct) where T : class
        {
            IQueryable<T> q = _db.Set<T>().AsNoTracking();
            var entityType = _db.Model.FindEntityType(typeof(T))!;
            var e = Expression.Parameter(typeof(T), "e");

            var entityScope = user.IsGlobalAdmin ? (int?)null : (user.EntityId ?? -1);
            var deptScope = user.IsGlobalAdmin || user.Role is "ADMIN" or "SYSADMIN" ? null : user.DepartmentId;
            q = WhereEquals(q, e, entityType.FindProperty("EntityId"), entityScope);
            q = WhereEquals(q, e, entityType.FindProperty("DepartmentId"), deptScope);

            if (!string.IsNullOrWhiteSpace(search))
            {
                Expression? any = null;
                foreach (var prop in entityType.GetProperties().Where(p => p.ClrType == typeof(string)))
                {
                    var call = Expression.Call(
                        Expression.Call(EfPropertyMethod.MakeGenericMethod(typeof(string)), e, Expression.Constant(prop.Name)),
                        typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!,
                        Expression.Constant(search.Trim()));
                    any = any is null ? call : Expression.OrElse(any, call);
                }
                if (any is not null) q = q.Where(Expression.Lambda<Func<T, bool>>(any, e));
            }

            var key = entityType.FindPrimaryKey()?.Properties.FirstOrDefault();
            if (key is not null)
            {
                // Newest first: order by the key in its own CLR type so EF can translate it.
                var keyAccess = Expression.Call(EfPropertyMethod.MakeGenericMethod(key.ClrType), e, Expression.Constant(key.Name));
                q = q.Provider.CreateQuery<T>(Expression.Call(
                    typeof(Queryable), nameof(Queryable.OrderByDescending), new[] { typeof(T), key.ClrType },
                    q.Expression, Expression.Quote(Expression.Lambda(keyAccess, e))));
            }

            var rows = await q.Take(top).ToListAsync(ct);
            return rows.Select(r => ScalarValues(r)).ToList();
        }

        private static IQueryable<T> WhereEquals<T>(IQueryable<T> q, ParameterExpression e,
            Microsoft.EntityFrameworkCore.Metadata.IProperty? prop, int? value)
        {
            if (prop is null || value is null) return q;
            var access = Expression.Convert(
                Expression.Call(EfPropertyMethod.MakeGenericMethod(prop.ClrType), e, Expression.Constant(prop.Name)),
                typeof(int?));
            var body = Expression.Equal(access, Expression.Constant(value, typeof(int?)));
            return q.Where(Expression.Lambda<Func<T, bool>>(body, e));
        }

        private Microsoft.EntityFrameworkCore.Metadata.IProperty? PrimaryKey(Type type)
        {
            var key = _db.Model.FindEntityType(type)?.FindPrimaryKey();
            return key is { Properties.Count: 1 } ? key.Properties[0] : null;
        }

        private static bool InScope(object record, AssistantUserContext user)
        {
            if (user.IsGlobalAdmin) return true;
            if (IntProperty(record, "EntityId") is { } entityId && entityId != user.EntityId) return false;
            if (user.Role is not ("ADMIN" or "SYSADMIN") && user.DepartmentId is { } dept
                && IntProperty(record, "DepartmentId") is { } recordDept && recordDept != dept)
            {
                return false;
            }
            return true;
        }

        private static int? IntProperty(object record, string name) =>
            record.GetType().GetProperty(name)?.GetValue(record) switch
            {
                int i => i,
                _ => null
            };

        private static Dictionary<string, object?> ScalarValues(object record)
        {
            var values = new Dictionary<string, object?>();
            foreach (var prop in record.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (AssistantWriteCatalog.SimpleType(prop.PropertyType) is null) continue;
                values[prop.Name] = prop.GetValue(record);
            }
            return values;
        }

        private static string RecordLabel(object record, long id)
        {
            var parts = record.GetType().GetProperties()
                .Where(p => p.PropertyType == typeof(string)
                            && (p.Name.EndsWith("Name") || p.Name.EndsWith("Code") || p.Name is "Description" or "Title" or "Subject"))
                .Select(p => p.GetValue(record) as string)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(3)
                .ToList();
            return parts.Count == 0 ? $"#{id}" : $"#{id} · {string.Join(" · ", parts)}";
        }

        private static string Title(WriteAction action)
        {
            var verb = action.Kind switch
            {
                WriteKind.Add => "Add / save",
                WriteKind.Edit => "Update",
                WriteKind.Delete => "Delete",
                _ => action.Action
            };
            return $"{verb} — {action.Controller} · {action.Action}";
        }

        /// <summary>
        /// Model binding reads form values in the server's current culture, so numbers are
        /// re-formatted for it; everything else is posted as text.
        /// </summary>
        private static string FormValue(JsonElement value, string type)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                case JsonValueKind.Number:
                    return type == "number" && value.TryGetDecimal(out var d) ? d.ToString(CultureInfo.CurrentCulture) : value.GetRawText();
                case JsonValueKind.String:
                    var s = value.GetString() ?? "";
                    return type == "number" && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
                        ? n.ToString(CultureInfo.CurrentCulture)
                        : s;
                default:
                    return value.GetRawText();
            }
        }

        private static string StoredValue(object value) => value switch
        {
            bool b => b ? "true" : "false",
            decimal d => d.ToString(CultureInfo.CurrentCulture),
            double d => d.ToString(CultureInfo.CurrentCulture),
            DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? ""
        };

        private static long? ToLong(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), out var n) => n,
            _ => null
        };

        private static long? Long(JsonElement args, string name) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) ? ToLong(v) : null;

        private static string? Str(JsonElement args, string name) =>
            args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

        private static string Json(object payload) => JsonSerializer.Serialize(payload);
    }
}
