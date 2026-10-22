# Governance reports and in-portal reminders

The application page links to **Governance-Bericht & Fristen**. All five existing
management mandates and central IAM see the same application-scoped counts.
Report visibility never grants access to another team's application.

## API and authorization

`GET /api/v1/applications/{id}/governance?offset=0&limit=25` requires the existing
authenticated BFF session. The current team mandate is checked inside the
application transaction before directory/review access. A foreign or removed
mandate receives `404`. Existing API `Cache-Control: no-store` applies.
This read-only GET does not need CSRF, contact Keycloak, write operations,
modify reviews or append audit events.

The response contains `applicationId`, `applicationRevision`, `asOf`, `counts`,
`totalNotices`, `notices` and `nextOffset`. Limits are 1–50, default 25;
offsets are 0–1,000,000. Invalid values return `400` after authorization.
Critical notices sort first, followed by warnings and information, then deadline
and stable ordinal key. Counts and totals use **all open reviews**, including
more than the historical review API's 200-row cap, before paging notices.

| Counter | Exact meaning |
| --- | --- |
| `assignments` | All persisted assignments in this application |
| `expiredAssignments` | Assignments with expiry at or before server time; no claim of remote removal |
| `unverifiableAssignments` | Target missing, duplicated, disabled, protected, malformed, stale by more than 15 minutes, over 1 minute in the future, or group membership incomplete |
| `groupSeparationRevocations` | Group assignments selected by the existing worker's `DirectoryGovernance.GroupRevocationsForSeparation`; not people or unique groups |
| `openReviews` | Non-closed reviews for this application |
| `pendingDecisions` | Pending items, including removed assignments still requiring review bookkeeping |
| `overdueDecisions` | Pending items whose review deadline is at or before server time |

Reliability is evaluated only for assigned targets. Reports include no directory
names, member keys, Keycloak IDs, free-text rationale or credentials. Each notice
contains a stable key, code, severity, count, optional deadline and review ID.
Decision permissions remain separate: reading a notice never grants approval or
revocation rights.

## Reminders and escalation cues

| Code | Threshold / action |
| --- | --- |
| `review_open` | Pending items, deadline more than 48 hours away |
| `review_due_soon` | Pending items, deadline within 48 hours, including the exact boundary |
| `review_overdue` | Deadline reached; inspect review and revocation state |
| `expired_assignments` | Expired persisted assignments remain; inspect worker operations |
| `directory_unverifiable` | Assigned identity snapshot cannot be verified |
| `group_separation_conflict` | Existing worker detector finds group SoD revocation candidates |
| `review_schedule_due` | Enabled schedule's next start reached; inspect worker execution |

Notices are recomputed on load, refresh and page navigation. Closed reviews and
resolved conditions disappear on the next successful read. The UI clears prior
data during refresh and on failure, and ignores late responses to earlier
requests. It sends no email/webhook, performs no background delivery, records no
acknowledgement and assigns no escalation recipient. External delivery remains
an open roadmap item.

## Consistency and limitations

Application, current team mandate, reviews and schedule use existing row locks.
The cached directory is read separately. `asOf` is the evaluation start, not an
atomic multi-system snapshot or live Keycloak read-back. Each page is recomputed;
concurrent worker activity can shift offset pages. Refresh from page one after
changes. `applicationRevision` does not version independent review/schedule
changes and is not an export consistency token.

Zero findings proves neither complete SoD compliance nor production acceptance.
Direct-user-only conflicts, unmanaged rights, existing JWTs, representative AD
behavior and external delivery guarantees need separate checks. The report
does not broaden remediation authority or delegated Keycloak token privileges.

## Checks

- `GovernanceReportingTests`: current/removed mandates, all five roles, more than
  200 reviews, exact time boundaries, stale/ambiguous/malformed directory,
  read-only state, and matching worker group-conflict detection.
- `PostgresReviewScheduleTests.Governance_counts_all_open_reviews_without_200_row_truncation_and_preserves_state`:
  real database, 211 open/one closed review, complete counts, bounded pages,
  foreign-actor denial and unchanged application/audit/operations.
- `scripts/ci-api-smoke.py`: real API/PostgreSQL owner/approver reads, invalid
  page bounds and foreign-team direct-route denial.
- `web/e2e/governance.spec.ts`: fixture-backed desktop/mobile counts, pagination,
  failed-refresh cleanup, retry and empty state.
- `web/accessibility/audit.spec.ts`: automated axe/reflow on desktop/mobile/320px;
  independent keyboard/screenreader acceptance remains separate.

See [access reviews](access-reviews.md), [CI](ci.md), [roadmap](roadmap.md)
and [release gates](release-checklist.md).
