# DeltaT design system — "ember console"

DeltaT is a measuring instrument for heat, styled like the performance
consoles that live on the machines it watches: warm blacks, one ember-orange
signal, chamfered plates, hazard stripes, bold condensed numerals. Under the flair it stays an instrument: every
visual decision still answers *does this help a technical user trust a
number?*

Non-negotiables carried from the product: dark only, temperatures are the
protagonist, color is never decoration — the gradient on a gauge arc is the
thermal scale itself, not paint.

Adversarial design QA lives in `DESIGN-AUDIT.md` — new screens must pass its
standing rules (accent discipline, earned signatures, honest empty states).

---

## 1. Color tokens

Warm soot surfaces (brown-black undertone, never pure black), one
interactive signal color (ember orange), and a strictly functional thermal
ramp. Green appears only as a verdict.

### Surfaces

| Token            | Hex       | Use |
|------------------|-----------|-----|
| `Bg`             | `#0E0A07` | window base (under `BgGlow` + dot grid) |
| `Rail`           | `#060403` | header bar — darkest layer |
| `Panel`          | `#17100A` | cards / modules |
| `PanelHigh`      | `#211711` | hover, raised rows, tooltips |
| `Inset`          | `#080504` | input wells, dial tracks — *below* Bg |
| `Stroke`         | `#332418` | 1px hairline borders |
| `StrokeSoft`     | `#251A11` | row separators, quiet rules |

Windows paint `Brush.BgGlow` (a low ember radial rising from the bottom
right — the console mood without shipping a bitmap) and overlay
`Brush.BgDots`, a 22px-pitch warm dot grid at ~4%. Panels sit on top, flat:
no shadows. Depth = layering + hairlines + brackets.

### Text

| Token       | Hex       | Use |
|-------------|-----------|-----|
| `Text`      | `#F2E8DC` | primary (warm white, never #FFF) |
| `TextDim`   | `#AE9884` | secondary, labels |
| `TextFaint` | `#6E5C4B` | captions, disabled, scale ticks |

### Signal (interactive accent)

| Token          | Hex       | Use |
|----------------|-----------|-----|
| `Accent`       | `#F26A1B` | active tab, focus, primary buttons, module titles, the live trace |
| `AccentBright` | `#FF9247` | hover on accent things |
| `AccentDim`    | `#38200E` | accent washes (selected fills, toggle tracks) |
| `AccentEdge`   | `#7A4218` | borders of accent-washed chips |

One loud element per screen. Ember is everywhere in small doses (titles,
underlines, ribbons) but only one *filled* ember element per screen.

### Thermal ramp (functional only)

| Token     | Hex       | Meaning |
|-----------|-----------|---------|
| `Cool`    | `#7FA2B8` | comfortably below limit (≤55% of throttle) — desaturated steel, the only cool hue in the app |
| `Warm`    | `#ECAF3A` | working hard (amber — always means "watch") |
| `HotWarn` | `#F4741F` | closing on the limit |
| `Hot`     | `#E93A2B` | at the limit / throttling / alerts |
| `Good`    | `#57C465` | verdict: healthy. Never used for temperature. |

The ramp is owned by `ThermalPalette.ColorFromFraction` (fraction =
temp/throttle-limit) so steel→amber→orange→red means the same thing in the
tray icon, gauges, ledger numerals and charts. Yes, `HotWarn` sits next to
`Accent` — in this language orange *is* the house color and heat is the
subject; the ramp still disambiguates because readings start steel.

Verdict bands (`VerdictColor`): ≥85 Good · ≥70 Cool · ≥50 Warm · ≥30 HotWarn
· else Hot.

---

## 2. Typography

Three voices, each with a job:

| Role | Family | Where |
|------|--------|-------|
| **Display** | `Bahnschrift` (DIN 1451) | tab caps, H1s, overline labels |
| **Display numerals** | `Bahnschrift Condensed` **Bold** | hero numerals, dial digits, stat strips — the chunky gauge voice |
| **Data** | `Cascadia Mono` → `Consolas` | every live value, timestamp, axis label, delta — anything that updates |
| **Body** | `Segoe UI Variable Text` → `Segoe UI` | sentences |

Bahnschrift is the engineering standard typeface (DIN), ships with
Windows 10 1709+, and its bold condensed cut is the closest built-in voice
to a gaming-HUD numeral. Data stays monospaced so updating values don't
jitter.

### Scale

| Style | Face | Size | Notes |
|-------|------|------|-------|
| Numeral.Hero | Bahnschrift Condensed Bold | 28–56 | dial centers, ledger temps at 28 |
| H1           | Bahnschrift SemiBold | 22 | verdict title |
| Overline     | Bahnschrift SemiBold 10.5 + letter tracking, **Accent** | section/module labels, ALL CAPS |
| Body         | Segoe UI 12.5–13 | line-height 19 |
| Data         | Cascadia Mono 10–12 | values, captions |

Overline tracking is done with the `ui:Tracking.Text` attached property
(interleaves hair spaces — WPF has no letterspacing).

---

## 3. Space, shape, strokes

- **4px base unit.** Padding steps: 8 / 12 / 16 / 20. Section gap 16–24.
- **Radius 0.** Corners are square or *cut*: `TechBorder` chamfers a corner
  at 45° (modules cut the top-right at 12px). Sharp, angular, panel-gauge.
- **Strokes are 1px, always.** Emphasis comes from brackets and ribbons,
  not weight.
- **Corner brackets:** thin ember L-marks hugging the top-right and
  bottom-left corners of a plate (`TechBorder Brackets="True"`). The card
  signature.
- **Hazard ribbon:** the `/////` stripe motif (`Brush.Hazard`, ~42×7px)
  ends every module header rule and follows the wordmark in the title bar.
  The design signature.
- **LEDs:** status dots are 6×6 squares, hard-cornered.
- No gradients on surfaces. The only gradients in the app are *data*: the
  gauge arc sweep and the brand delta.

---

## 4. Motion

- Gauge/dial arcs ease to new values over 400–600ms (CubicEase out). Nothing
  else animates continuously.
- View switches crossfade 140ms.
- No pulsing, no parallax. An instrument doesn't dance, even a gamer one.

---

## 5. Components

- **TechBorder:** chamfer-cornered Border with optional ember corner
  brackets. All plate chrome goes through it.
- **Module** (`HeaderedContentControl`, style `Module`): plate with ember
  overline + hairline rule + hazard ribbon, cut top-right corner, brackets.
  Replaces bare cards for any titled section.
- **ScoreDial:** 270° tick-scale dial. Minor ticks every 2 pts, major every
  10. Score = ticks lit in verdict color, brightening along the sweep, bold
  condensed numeral + verdict word. Calibrating = dim ember progress +
  "CAL n%".
- **RingGauge:** 270° chunky arc over a hairline track with scale ticks
  (last 15% tinted red). The value arc sweeps as a thermal gradient — steel
  at the start of scale, the current heat color at the tip. Gaming-gauge
  flair, but the gradient is data.
- **Sparkline:** warm-slate hairline trace; the newest point carries an
  ember dot (the "live" cursor).
- **TimeSeriesChart:** hairline horizontal grid, mono axis labels, min/max
  band as an ember wash, ember average trace, dashed warm-slate ambient,
  event markers as letter-tagged verticals, crosshair readout in a
  PanelHigh box.
- **Buttons:** ghost by default (mono caps, cut corner); hover lights the
  edge ember. Primary = *filled* ember plate, bold caps — the selected-plate
  language. One primary per screen.
- **Nav:** top tab bar in the title bar (console header): tracked DIN
  caps, 2.5px ember underline + bright text when active.

## 6. Screens

1. **Dashboard** — verdict hero (paste diagnosis + CPU/GPU score dials) over
   the live telemetry ledger; remark ticker pinned at the bottom.
2. **Trends** — full-bleed chart module, channel/range segments, keyline
   stat strip.
3. **Remarks** — field log list, severity LEDs, mono timestamps.
4. **Device** — detected hardware identity, profile expectations vs live
   measurement, silicon limits. (The "device intelligence" screen.)
5. **Settings** — two-column modules.
6. **Onboarding / Fingerprint** — same language, one primary action each.
