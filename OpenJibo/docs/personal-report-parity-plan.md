# Personal Report Parity Plan

As-of: `2026-05-07`

## Objective

Bring OpenJibo personal report behavior closer to original Jibo charm while keeping cloud architecture modern and provider-agnostic.

## Pegasus Findings (Source Anchors)

- Weather personality and visuals were MIM-driven, not plain speech:
  - `C:\Projects\jibo\pegasus\packages\report-skill\src\subskills\weather\WeatherMimLogic.ts`
  - `C:\Projects\jibo\pegasus\packages\report-skill\mims\en-us\WeatherCommentRain.mim`
  - `C:\Projects\jibo\pegasus\packages\report-skill\mims\en-us\WeatherTodayHighLow.mim`
  - `C:\Projects\jibo\pegasus\packages\report-skill\resources\views\weatherHiLo.json`
- Weather icons were mapped to condition/time-of-day tokens (`clear-day`, `partly-cloudy-night`, etc.) and used in `<anim cat='weather' meta='...'>`.
- Report-skill supported reactive entrypoints beyond full personal report:
  - `requestWeatherPR`, `requestNews`, `requestCommute`, `requestCalendar`
  - Source: `C:\Projects\jibo\pegasus\packages\hub\pegasus-skills\report_skill_manifest.json`
- Legacy data backends were Lasso-mediated:
  - weather: Dark Sky
  - commute: Google Maps directions/traffic
  - news: AP News feeds
  - calendar: Google/Outlook connectors
- Parser `main_agent` explicitly includes weather/news/personal-report intents; direct commute/calendar intents are not present in that same folder snapshot:
  - `C:\Projects\jibo\pegasus\packages\parser\dialogflow\main_agent\intents`
- Grocery/list behavior found in Pegasus is scripted-response style, not a standalone list skill:
  - `RA_JBO_ShoppingList.mim` and `RA_JBO_ManageToDoList.mim` are "not supported yet" style responses.

## OpenJibo Current State

- Personal report state machine exists and is test-backed.
- Weather provider integration exists (OpenWeather), including current and tomorrow.
- News and commute currently have baseline placeholder speech, not live provider-backed data orchestration.
- Calendar is currently reply-based and not yet provider-integrated.

## Gap Summary

1. Weather has factual speech but needs stronger visual/personality parity.
2. Non-local weather and broader date scopes need expansion beyond basic trailing `in <location>` and tomorrow handling.
3. Live news feed selection and filtering strategy is not yet implemented.
4. Commute data path and settings model are not yet mapped to an active provider integration.
5. Full personal report parity matrix (weather/commute/calendar/news behavior details) is not yet documented as a ship checklist.

## Implementation Phases

## Phase 1 (In Progress): Weather Personality Lift

- Add weather-condition animation metadata and expressive weather MIM-style prompt metadata to cloud weather speech.
- Expand location phrase handling (`in/for/at`) and suffix stripping for common temporal tails.

## Phase 2: Weather Visual Layer Parity

- Add weather Hi/Lo view payload support (OpenJibo-side equivalent to `weatherHiLo.json` behavior).
- Carry mapped weather icon token + hi/lo values into outbound skill action config.
- Keep fallback behavior safe when view assets are unavailable.

## Phase 3: Weather Scope Expansion

- Add parser support for additional time requests (for example weekend/next-week phrasing).
- Extend weather request model to support short-range date windows.
- Decide whether range responses are summarized speech-only or include multi-card view behavior.

## Phase 4: Live News Source

- Introduce provider-backed headline ingestion with category toggles.
- Mirror core Pegasus constraints:
  - de-duplicate headlines
  - filter missing summaries/images
  - child-safe filtering mode
- Preserve current speech fallback if provider is unavailable.

## Phase 5: Commute Data Path

- Implement commute provider abstraction and first provider integration.
- Recreate core commute decision logic:
  - minutes-left
  - normal vs delayed traffic commentary
  - mode-aware phrasing (drive vs transit)
- Add settings contract for origin/destination/work-arrival/mode.

## Phase 6: Personal Report Coverage Matrix

- Build parity matrix across weather/news/commute/calendar:
  - intent phrases
  - required entities/settings
  - provider dependencies
  - expected MIM/view style outputs
  - fallback behavior
- Attach tests and capture criteria for each row.

## Phase 7 (Future Release): Grocery Lists

- Track as a future release item (requested by users).
- Two candidate paths:
  1. Native lightweight list skill (fastest to ship).
  2. Integration-backed list orchestration (better long-term ecosystem fit).
- Recommendation: ship native MVP first, then add integration connectors.

## Next Immediate Execution

1. Validate weather personality-lift behavior in live runs.
2. Implement weather view payload support (Hi/Lo + condition icon).
3. Draft provider plan for live news source.
4. Draft commute provider interface + settings schema.
