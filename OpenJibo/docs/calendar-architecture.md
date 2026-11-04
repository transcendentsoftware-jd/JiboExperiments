# Calendar Architecture

Pegasus treated calendar as a loop-scoped report surface, with report output fed by the
household context instead of an isolated generic calendar service.

In OpenJibo, the current calendar path follows the same broad shape:

- calendar report output is loop-scoped
- the report provider can read persisted loop calendar events
- birthday and other personal dates already live in the loop-scoped holiday list
- the personal report merges the report provider output into the spoken flow

The original loop-event provider remains the fallback. The configured iCalendar
provider reads the member's feed through the ownership-bound integration store,
fetches and caches the result, and reports events for today and tomorrow.

Fallback behavior:

- if loop calendar events exist, the provider surfaces the next matching items
- if no loop calendar events exist, the provider falls back to the merged holiday list
- birthdays and custom holiday entries can therefore appear in the calendar section
- the personal report still degrades safely when no calendar data is available

Notes:

- the current provider is intentionally lightweight and in-process
- this gives us a swappable seam for later Azure-backed calendar sync
- commute remains a separate report gap for the next pass

## Weekly iCalendar recurrence checkpoint (2026-11-04)

Weekly events now expand every selected `BYDAY`, rather than stepping only on
the starting weekday. For example, `FREQ=WEEKLY;BYDAY=MO,WE,FR` includes Wednesday
and Friday as well as Monday. Old series no longer disappear after 366 weeks.
Expansion retains the event's UTC, named `TZID`, or floating wall time until
conversion to the requested display zone. All-day dates remain calendar dates.

The supported weekly subset is `FREQ=WEEKLY`, positive `INTERVAL`, simple
weekday `BYDAY`, `WKST` (Monday by default), and either positive `COUNT` or
inclusive `UNTIL`. Count includes earlier occurrences and skips nonexistent
daylight-saving times; an ambiguous time uses its first occurrence. UTC and
named-zone starts require UTC `UNTIL`, floating starts require floating
`UNTIL`, and date starts require date `UNTIL`. These rules follow
[RFC 5545, recurrence rules](https://datatracker.ietf.org/doc/html/rfc5545#section-3.3.10).

Unsupported or malformed weekly rule fields, duplicate fields, ordinal weekdays,
unknown time-zone identifiers, and a start weekday inconsistent with `BYDAY`
produce no occurrences. They are not silently treated as a simpler rule.
Normal series use direct week arithmetic over dates near the requested window.
Only `COUNT` series whose wall time can fall in a daylight-saving gap need
transition history; that calculation is limited to 400 years, after which the
series is declined. This is a bounded subset, not a complete RFC recurrence engine.

Remaining gaps:

- the report provider still chooses the server's local zone for today/tomorrow;
  robot/household zone selection needs a separate change
- custom `VTIMEZONE` definitions, `EXDATE`, `RDATE`, recurrence overrides and
  recurring multi-day spans are not implemented by this change
- daily and monthly expansion retain their existing implementation and limits
- skipped unsupported rules do not yet surface a feed-level diagnostic
- Pegasus report data came from provider-expanded Google/Outlook events;
  this parser fix does not establish full personal-report parity or live-robot proof

Regression coverage includes multiple weekdays, interval/week-start boundaries,
count exhaustion, long-running series, UTC day shifts, named-zone daylight-saving
transitions, inclusive end limits, all-day dates and unsupported-rule refusal.
