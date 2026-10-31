GovBudget guide screenshots
===========================

Referenced by the two guides in ../ :
  GovBudget_Guide_EN.html   (English)
  GovBudget_Guide_AR.html   (Arabic — same figures, same filenames)

Both guides use src="images/<name>", so one file serves both languages. Each
guide currently shows 11 figures and every one of them is present.


CURRENT SET
-----------
  01-login.png                 Sign-in page
  02-executive-summary.png     Home — Executive Summary
  03-budget-setup-context.png  Budget Setup — year / entity / cost centre
  04-budget-entry.png          Budget Entry — OPEX tab
  05-admin-room.png            Admin Room card hub
  06-edit-user.png             Edit User — role, entity, cost centre
  07-hr-allocate.png           HR — allocating one employee across activities
  08-submissions.png           Budget Submissions list with status tabs
  09-work-calendars.png        Work Calendars — hour build-up and coverage
  10-cost-per-hour.png         Employee Cost per Hour report
  11-report-builder.png        Report Builder showing a waterfall chart


HOW 02-11 WERE CAPTURED
-----------------------
Automated, from the application running locally against the live database, so
they show the current navy theme and the current feature set:

  Viewport   1400 x 1000 at deviceScaleFactor 2 (2x, so they stay sharp in print)
  Signed in  as a global ADMIN, so the full sidebar is visible
  Context    year 2026, entity RDAM (Antiquities and Museums), cost centre 3
  Theme      light
  Charts     captured after a short delay, because Chart.js animates on entry and
             a screenshot taken immediately shows an empty plot area

01-login.png predates the others and was deliberately NOT retaken: the sign-in
page has no sidebar, so the theme change did not affect it.

Two notes on what the images show:

  * The sidebar reads "Logged in as: Administrator". The capture ran under a
    temporary account that has since been deleted; the label was replaced at
    capture time so the figures would not carry a throwaway account name.

  * The figures contain real employee names and real salary figures from RDAM.
    That is fine for internal use. Review before sending the guides outside the
    organisation.


TO RECAPTURE
------------
Run the app locally (it must be the local build — the live site may be a
deployment behind), sign in as an administrator, set the context above, and
match the viewport settings so the new images sit consistently beside the ones
you keep.
