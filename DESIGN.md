# Race Command design system

The launcher is an operational race console used beside Motorsport Manager. It
prioritizes connection state, driver selection, live telemetry, and immediate
session commands over installation details.

## Visual language

- Canvas: near-black graphite `#0D1014`.
- Primary surface: `#181D23`; raised controls: `#20262D`.
- Main text: `#EEF2F4`; secondary text: `#9FAAB2`.
- Live/ready accent: signal green `#4EDA80`. Red is reserved for connection loss.
- Typography: Segoe UI with Semibold for operational headings.
- Controls are rectangular with small native radii and no decorative shadows.

## Layout

The desktop shell uses a narrow Host/setup rail and a flexible race-command
workspace. Connection and session state remain above the driver selector. Practice,
Qualifying, and Race are separate tabs with controls relevant to each phase.

## Interaction

The controller connects on startup. “Обновить состояние” requests an immediate
telemetry snapshot and reconnects if needed. Incoming session names select the
matching tab automatically while preserving manual tab access.
