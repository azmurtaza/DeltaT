# Design audit — machine-generation tells

An adversarial pass over the ember-console UI: everything that could read as
template output rather than a crafted product, itemized with evidence, then
resolved. Statuses reflect the current tree. Keep this list alive — new
screens get audited against the same categories before they ship.

## A. Composition

| # | Finding | Evidence | Resolution |
|---|---------|----------|------------|
| A1 | Accent applied by rule, not by eye: ember at uniform intensity on every module title, every delta readout, every load bar, every sparkline dot. No quiet zones, so nothing is loud. | dashboard screenshot, v2 | **Fixed.** Ledger deltas demoted to `TextDim`; load bars recolored bronze `#8A5A2C` (load is effort, not heat, not interaction); ribbons/brackets reserved for lead modules. Ember now means: interactive, live, or the screen's one subject. |
| A2 | Signature motif stamped on 100% of containers — identical 42×7 hazard ribbon, identical brackets, identical 12px chamfer on every plate of every screen. A designer varies the motif; a generator loops it. | every module, v2 screenshots | **Fixed.** New `Module.Quiet` style (dim title, bare rule, no ribbon/brackets). Lead module or matched pair per screen keeps the full signature: Dashboard→Live telemetry, Device→the two expected-vs-measured plates, Settings→Location & weather. Support panels went quiet. |
| A3 | Two identical dials reading "0%" side by side; a bold "0%" numeral reads *broken*, not *learning*. | dashboard hero, v2 | **Fixed.** While calibrating the dial reads `--` (faint) with `CAL n%` beneath; the tick sweep still fills with learning progress. A meter that has no reading says so. |
| A4 | Hazard ribbon beside the wordmark = decoration without function. | title bar | **Overruled, kept.** It is the brand's registration mark and appears exactly once in chrome. Documented in DESIGN.md as intentional. |

## B. Craft

| # | Finding | Evidence | Resolution |
|---|---------|----------|------------|
| B1 | Mid-word ellipsis in the ledger ("Simulated Core i5-134…") — column widths were guessed, not fitted. | dashboard, v2 | **Fixed.** Identity column re-fitted (30px glyph + 158px text), load column tightened; full names now fit at this window size. |
| B2 | The string "Δ +N° vs outside" repeated verbatim on every row — template echo; units and references are stated once on instruments. | dashboard ledger | **Fixed.** Ledger gained a caption row (`COMPONENT · LOAD · TRACE · Δ = RISE OVER OUTSIDE`); rows now read `Δ +18.2°`, with a ` vs room` suffix only when the indoor offset makes the reference differ. |
| B3 | Zero iconography anywhere — text-only badges are the classic poverty of generated UIs; the genre this app lives in is icon-rich. | all screens | **Fixed.** `ComponentGlyph`: stroke-drawn silkscreen glyphs (chip / card+fan / M.2 stick / cell) on a 16-grid, quiet label color, in the ledger identity column. They identify, they don't decorate. |
| B4 | Chart empty state is a lowercase prose string floating in a void. | trends, empty range | **Fixed.** Empty state prints like an instrument: tracked caps `NO SAMPLES IN THIS RANGE` on the centerline with flanking hairline dashes. |
| B5 | Two window languages in one app: main window custom chrome (no maximize, no icon) vs. fingerprint window stock Windows chrome. Assembled-in-passes tell. | window chrome | **Fixed.** Main window gained maximize/restore (with maximized-margin correction) and the ember `.ico`; the fingerprint window now carries the same 38px chrome bar (mark + tracked title + close). |

## C. Voice

| # | Finding | Evidence | Resolution |
|---|---------|----------|------------|
| C1 | The onboarding triad — three paragraphs each opening with an accent-colored em-dash lead-in ("What it does — ", "Privacy — ", "The learning week — "). Recognizable generated-prose scaffolding. | onboarding | **Fixed.** Restructured as quiet overline labels above plain prose; em-dash lead-ins removed. Same facts, instrument layout. |
| C2 | Dingbats inside data strings: "✓" embedded in ViewModel status text, "▪" bullets in the fingerprint checklist. | OnboardingViewModel, FingerprintWindow | **Fixed.** Words instead of glyphs ("Location set: …"); the checklist became a labeled paragraph under `BEFORE YOU START`. |
| C3 | Anthropomorphic tagline "Your machine's thermal conscience." | onboarding hero | **Fixed.** Now "A meter for thermal-paste health." — says what it is. Personality stays in the remarks feed, where the app is *supposed* to speak. |

## D. Repository

| # | Finding | Evidence | Resolution |
|---|---------|----------|------------|
| D1 | A competitor's product name cited throughout code comments and the design spec — prompt residue, and borrowed identity in a competition setting. | 15 occurrences across src/ + docs | **Fixed.** The language stands on its own vocabulary now: *ember console*, *plates*, *hazard ribbon*, *gauge sweep*, *console header*. Zero product-name references in the tree. |
| D2 | The same magic literal `#8CF26A1B` pasted in three places (module template, hero plate, theme) instead of a token. | DeltaT.xaml, DashboardView | **Fixed.** `Col.Bracket` / `Brush.Bracket` token; all bracket chrome references it. |

## Standing rules distilled from this audit

1. Ember means *interactive, live, or the subject* — never "section heading" by default.
2. The signature (ribbon + brackets) is earned by one lead module per screen.
3. A reading that doesn't exist is drawn as `--`, never as a zero.
4. Units and references are stated once per surface, not per row.
5. No dingbats in strings; state changes are words.
6. Every container's width is fitted to its real content at the design size.
7. The design language is described in its own vocabulary.
