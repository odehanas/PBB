# OECD Performance-Based Budgeting Reference

Offline reference used by the in-app assistant (`search_pbb_reference`). It summarises
publicly documented OECD guidance on performance budgeting and the standard ratios used in
this application. It is a summary for orientation, not an official OECD publication; for the
authoritative text see the OECD sources listed at the end, which the assistant can fetch
live with `oecd_read_page`.

## What performance-based budgeting is

Performance budgeting links the funds allocated to a public organisation with measurable
results. The OECD distinguishes three broad forms:

- **Presentational** — performance information is published alongside the budget but is not
  used in allocation decisions.
- **Performance-informed** — performance information is one input, weighed with policy
  priorities and fiscal constraints, into the allocation decision. This is the model most
  OECD members apply and the model this application supports.
- **Direct / formula** — allocations are mechanically tied to results (unit price × volume).
  Rare, and normally limited to sectors with well-defined outputs such as health procedures
  or student places.

The budget is structured by **programme** (a policy objective) and **activity** (the work
that delivers it), rather than only by organisational unit and line item. Costs are captured
against activities so a cost per unit of output can be derived.

## OECD good practices for performance budgeting

Recurring recommendations in OECD work on performance budgeting and the OECD Best Practices
for Budget Transparency:

1. Keep the number of indicators small and stable; a handful of well-chosen indicators per
   programme beats dozens that nobody reviews.
2. Cover the full results chain — inputs, outputs, outcomes — and label each indicator
   explicitly, because most disputes come from confusing outputs with outcomes.
3. Define each indicator once: name, unit, calculation method, data source, direction of
   improvement, baseline and target. Change definitions between years only deliberately.
4. Give the finance ministry and the line ministries a shared, single source of performance
   data, updated on the budget calendar (typically mid-year and year-end).
5. Use performance information in dialogue, not as an automatic penalty. Poor results may
   justify more funding (capacity problem) as often as less (design problem).
6. Publish performance results with the budget documents so the legislature and the public
   can see them; transparency is what makes the information credible.
7. Quality-assure the data: an internal or external audit of a sample of indicators each
   year is the usual mechanism.
8. Match ambition to maturity — start with a presentational stage, then move to
   performance-informed once definitions and data are stable.

## Indicator types and dimensions

- **Input** — resources consumed (staff hours, budget spent).
- **Output** — what the activity produces and directly controls (permits issued, inspections
  completed, kilometres maintained).
- **Outcome** — the change in the wider situation the programme is meant to influence
  (road fatality rate, business satisfaction). Influenced by external factors, so it is
  attributed to the programme, not controlled by it.
- **Efficiency dimension** — output achieved relative to input, i.e. cost or time per unit.
- **Quality dimension** — the standard of the output: accuracy, timeliness, user satisfaction,
  complaint or rework rate.

Direction of improvement must be stated for every indicator: `UP` where higher is better
(satisfaction), `DOWN` where lower is better (processing time, cost per case).

## Standard ratios and formulas

Financial execution and control:

| Ratio | Formula | Reading |
| --- | --- | --- |
| Budget execution rate | Actual expenditure ÷ approved budget × 100 | 95–105% is normally treated as on-plan; persistent under-execution signals over-budgeting or delivery problems |
| Budget variance | Approved budget − actual expenditure | Positive = underspend, negative = overspend |
| Absolute variance rate | \|variance\| ÷ budget × 100 | Used to rank the lines worth investigating |
| In-year revision rate | Net value of reallocations ÷ original budget × 100 | High values suggest weak initial budgeting |
| Capital execution rate | Actual CAPEX ÷ budgeted CAPEX × 100 | Usually the weakest execution area; reported separately from OPEX |
| Revenue realisation rate | Actual revenue ÷ budgeted revenue × 100 | |

Efficiency and performance:

| Ratio | Formula | Reading |
| --- | --- | --- |
| Unit cost / cost per output | Activity cost ÷ output volume | The core PBB efficiency measure; compare across years and across peer units, not across unlike services |
| Cost per outcome | Programme cost ÷ outcome units delivered | Use with care; outcomes are influenced by factors outside the programme |
| KPI achievement (UP) | Actual ÷ target × 100 | 100% = on target |
| KPI achievement (DOWN) | Target ÷ actual × 100 | 100% = on target, so both directions read the same way |
| Share of programmes with performance information | Programmes with at least one active KPI ÷ total programmes × 100 | A standard PBB coverage measure |
| Share of expenditure covered by programmes | Expenditure mapped to a programme ÷ total expenditure × 100 | Coverage of the programme structure |
| Administrative cost ratio | Support/overhead cost ÷ total cost × 100 | Depends on the allocation of shared costs, so state the allocation basis |
| Personnel cost ratio | Staff cost ÷ total operating cost × 100 | |
| Forecast accuracy | \|forecast − actual\| ÷ actual × 100 | Applied to the mid-year forecast |

## Reading unit-cost comparisons

A rising unit cost is not automatically bad. Check, in order: whether the output definition
or counting method changed; whether volume fell while fixed costs stayed (a scale effect);
whether quality indicators moved in the opposite direction (cost cut at the expense of
service); and only then whether efficiency genuinely deteriorated. Compare like with like —
same output measure, same cost scope (direct only, or direct plus allocated overhead).

## PBB maturity stages

A common five-stage maturity scale used to assess entities:

1. **Initial** — budget by line item only; no programme structure.
2. **Developing** — programmes and activities defined; a few indicators, inconsistent data.
3. **Defined** — full programme structure, documented indicator definitions, costs mapped to
   activities, results reported on the budget calendar.
4. **Managed** — unit costs and results are reviewed in budget negotiations; targets are
   evidence-based; data quality assured.
5. **Optimising** — multi-year performance framework, spending reviews, evaluation feeding
   back into allocation.

## Budget transparency expectations

OECD budget-transparency practice expects the published budget to include: the medium-term
fiscal framework and its assumptions; programme-level allocations with objectives and
indicators; prior-year results next to the new year's targets; and clear disclosure of
significant reallocations during the year. Performance information should be published in
the same document as the money, not in a separate report issued later.

## Where the live OECD sources are

- OECD Data Explorer (statistics, SDMX API): `https://data-explorer.oecd.org` and
  `https://sdmx.oecd.org/public/rest`
- OECD budgeting and public expenditure topic pages: `https://www.oecd.org`
- OECD Journal on Budgeting and Working Papers on Public Governance, on the same site.

When a question needs a current figure or the exact wording of OECD guidance, the assistant
should read those sources with `oecd_data_query` / `oecd_read_page` and cite the URL.
