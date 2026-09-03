# ClipBridge Windows UI (Milestone 4)

## Architecture

Polished WinForms, in the existing Windows process. WPF was unnecessary: the existing
STA message loop, hidden clipboard window and ApplicationContext provide the right
lifetime. `TrayContext` owns a lazily created `MainWindow`; closing that view never stops
the engine. No browser, JavaScript runtime, new service or UI polling loop.

`ClipBridge.Win/UI/` contains presentation mapping, appearance persistence, drawn
controls, the window and a safe clipboard adapter. `ClipBridge.Core`, the Python relay
and Shortcuts contracts are unchanged. A separate `RelayClient` reads history. Requests
are canceled on leaving History, hiding the window or exiting; stale responses cannot
repopulate a hidden page. No retention or persistence semantics change.

One existing host bug was fixed in `Program.cs`: standalone autostart-command detection
also matched `--tray-autostart` and returned before starting the app. Only the three
actual registry commands now take that branch; sign-in startup runs quietly in the tray.

## Design

Quiet Native Utility: compact navigation, Segoe UI, subdued surfaces, rounded geometry,
restrained borders and line icons. Default client area: 704 × 604 logical pixels; minimum
outer size: 720 × 620. Native title bar, resize and taskbar behavior remain. The original
clipboard/transfer icon has 16/20/24/32/48/64/128/256-pixel ICO entries; its source SVG and
repeatable System.Drawing generator are included.

## State bindings

`AppStatusModel.Changed` is marshaled through the existing hidden STA window. The UI never
parses logs. `StatusPresentation` prioritizes Starting, Relay restarting, Offline, Paused
and No local network before Connected. Connected means relay readiness with a usable
LAN address, **not iPhone presence**. The phone endpoint always says “via Shortcut”.
Pause/Resume and Check now use the existing engine APIs; no offline queue is introduced.

Home shows at most two observed session transfers, using direction, byte size and time.
No clipboard text is loaded into Home. Relative times update on status changes and window
activation; there is no timer merely to keep “Just now” ticking.

The IP URL comes from `AppStatusSnapshot.LanUrl` and the existing physical-LAN resolver.
It updates on network events. `.local` is not promoted: the experiment failed on the
user's iPhone, and IP remains the supported connection method.

## History and privacy

History calls existing `GetRecentAsync(50)`. Rows show source → Relay, received time,
size and a bounded one-line preview. A stored record proves arrival at Relay, not
delivery to Windows. Unknown devices keep their device ID; no iPad surface is added.

Nothing is automatically selected. Selection opens a read-only native plain-text reader
with visual line wrapping and an event-driven themed scrollbar. Markdown is not
interpreted. Copy text uses the original record string, preserving Unicode and whitespace
exactly. Keyboard/context-menu selection copy uses the native reader's selected text,
which can normalize line endings. Leaving History or hiding clears preview and full-text
references from the view; history remains in the existing relay database. No clipboard
content logging, analytics or telemetry is added.

All app copy actions reuse `SyncEngine.SuppressNextLocalEvents`. `SafeClipboardCopy`
remembers the OS sequence number of the app's write, so duplicate Windows notifications
receive the same suppression. A fresh user write has a new sequence even for identical
text. Paused notifications consume suppression; failed writes roll it back. Neither
swallows the next ordinary copy. No relay bypass or hashing change is involved; the
sequence is metadata and does not retain clipboard contents.

## Appearance

System is the default; Light and Dark are explicit overrides. Only the theme is saved,
atomically, in `%APPDATA%\ClipBridge\appearance.json`, separate from token/config data.
System follows `AppsUseLightTheme`; preference events refresh the palette, title bar and
tray menu. Accent is sparse and uses a contrasting foreground. Dark has distinct
surfaces, separators, selection and text hierarchy. Tooltips and the reader scrollbar
are themed. Windows High Contrast uses system colors.

## Window, tray and keyboard

Normal launch opens Home. Sign-in launch (`--tray-autostart`) stays in the tray. Tray
left-click/double-click, Open ClipBridge and a second launch focus the same window.
Close/Escape hide it; Exit ClipBridge stops the existing lifecycle in the existing order.
Startup state is re-read from HKCU on opening/focusing the window and opening the tray
menu. Restart relay appears in Settings only when the existing manager reports failure.
The same application menu is available by right-clicking the window or using Shift+F10,
so its actions remain reachable when Windows hides the tray icon in the overflow.

Tab/Shift+Tab navigate, Space/Enter activate standard Button controls, and Ctrl+1/2/3
select Home/History/Settings. Buttons have accessible names and keyboard focus; status
uses words and color. History uses a native accessible ListBox and RichTextBox. Its
scrollbar supports arrows, Page Up/Down, Home/End and dragging. PerMonitorV2 DPI awareness
combines logical layout and native font scaling.

## Review and limitations

Automated checks cover state precedence, bounded Unicode previews, theme persistence,
duplicate/paused/failed clipboard writes, new equal-text copies, privacy on hide,
empty/error History, minimum-size containment, and text metrics at 96/120/144/192 DPI.
Manual review uses the daily executable; screenshots and review notes live in
`artifacts/ui-review/` and are intentionally not committed.

- English UI; Unicode clipboard content.
- No live iPhone presence, delivery receipts, rich Markdown, history search or deletion UI.
- No offline queue, retention change or networking redesign.
- Basic screen-reader labels are provided; no comprehensive assistive-technology or
  mixed-monitor hardware certification was performed.
- Malformed-history responses retain the existing RelayClient behavior.
- The original missing-token dialog remains; no pairing/setup wizard.
- Real iPhone regression: `READY_FOR_USER_RETEST`.
