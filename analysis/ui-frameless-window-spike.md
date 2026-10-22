# Frameless shell S0 spike: real-Windows results

> **Investigation/prototype only.** No product code, `App.axaml`, `WorkbenchWindow`, dialogs,
> tests, CI, packages or existing reports were changed. The throwaway prototype lives outside the
> repository (`C:\projects\ai_work\frameless-spike-proto`) and is not committed; this branch
> contains this report only.
>
> - Base: `5183a0fc456acbe3a475ab221b123068f0ab07c5` ("Cover active perspective and hover contrast tokens")
> - Branch: `agents/ui/frameless-spike`, worktree `C:\projects\ai_work\agent-wt\frameless-spike`
> - Design under test: `analysis/ui-frameless-window-design.md` on `agents/ui/frameless-design`
>   (not present on this branch; commit `33ee3dc`) — variant A
> - Environment: Windows 11 Pro 25H2, build `10.0.26200`, 1920 × 1200 at **100 %** scaling,
>   Avalonia `12.1.4`, `net9.0`, single monitor, desktop session
> - Prototype: minimal Avalonia window with the exact variant-A settings, a 49 px strip with a
>   `TitleBar` border, a `User` menu + button, and three 46 × 49 caption buttons with roles;
>   plus a Win32 input driver and several focused PowerShell probes
> - Evidence: `C:\projects\ai_work\frameless-spike-proto\evidence\**` (screenshots, logs) —
>   outside the repo, not committed
>
> **Verdict: proceed with variant A only with three mandatory corrections (§7).** The design's
> platform contract holds for drag, double-click, resize, snap layouts, single-click maximize,
> Alt+F4 and the custom buttons, but three claims in the design are wrong as written: the
> no-template decorations theme does not suppress Fluent's drawn caption, the system menu cannot
> be opened on the extended strip, and its Close item is greyed by Avalonia. If a native system
> menu on the caption strip is a hard requirement, keep the native title bar instead.

---

## 1. What was actually run

A throwaway app (`Spike`) and a Win32 input driver (`Driver`), both outside the repository:

| Piece | Purpose |
|---|---|
| `Spike` (Avalonia 12.1.4, `net9.0`, WinExe) | the variant-A window: `WindowDecorations=Full`, `ExtendClientAreaToDecorationsHint=True`, `ExtendClientAreaTitleBarHeightHint=49`, custom decorations theme, 49 px strip, role-marked menu/button/caption buttons; logs window styles, hit tests, geometry and every state change |
| `Driver` (`net9.0-windows`) | real `SendInput` mouse/keyboard: drag, double-click, resize, button clicks, Alt+Space, Alt+F4; screenshots; reads the app log |
| `sysmenu-probe.ps1`, `rclick-probe.ps1`, `snap-probe.ps1`, `extras-probe.ps1` | focused probes: system menu (frameless vs plain window), right-click positions, snap-layout hover, UIA tree, high contrast |

Everything below is measured, not inferred, unless a line says otherwise.

### 1.1 Exact API surface that compiles (verified)

```xml
<Window x:Class="Spike.MainWindow"
        xmlns:chrome="using:Avalonia.Controls.Chrome"
        Title="Frameless Spike"
        WindowDecorations="Full"
        ExtendClientAreaToDecorationsHint="True"
        ExtendClientAreaTitleBarHeightHint="49"
        WindowDecorationsTheme="{StaticResource WbWindowDecorationsTheme}">
```

```xml
<!-- Application.Resources: the only form that actually suppresses Fluent's caption (see F1). -->
<ControlTheme x:Key="WbWindowDecorationsTheme" TargetType="chrome:WindowDrawnDecorations">
  <Setter Property="DefaultTitleBarHeight" Value="49" />
  <Setter Property="Template">
    <WindowDrawnDecorationsTemplate>
      <WindowDrawnDecorationsContent>
        <WindowDrawnDecorationsContent.Overlay>
          <Panel Height="{TemplateBinding TitleBarHeight}" IsHitTestVisible="False" />
        </WindowDrawnDecorationsContent.Overlay>
      </WindowDrawnDecorationsContent>
    </WindowDrawnDecorationsTemplate>
  </Setter>
</ControlTheme>
```

```xml
<!-- strip + roles -->
<Border x:Name="Topbar" chrome:WindowDecorationProperties.ElementRole="TitleBar">
  <Grid ColumnDefinitions="Auto,Auto,*,Auto">
    <Menu chrome:WindowDecorationProperties.ElementRole="User"> … </Menu>
    <Button chrome:WindowDecorationProperties.ElementRole="User"> … </Button>
    <StackPanel>
      <Button chrome:WindowDecorationProperties.ElementRole="MinimizeButton" Width="46" Height="49"> … </Button>
      <Button chrome:WindowDecorationProperties.ElementRole="MaximizeButton" Width="46" Height="49"> … </Button>
      <Button chrome:WindowDecorationProperties.ElementRole="CloseButton"      Width="46" Height="49"> … </Button>
    </StackPanel>
  </Grid>
</Border>
```

`WindowDecorationsElementRole` values, read from the running assembly
(`Enum.GetNames<WindowDecorationsElementRole>()`):

```
None, DecorationsElement, User, TitleBar, ResizeN, ResizeS, ResizeE, ResizeW,
ResizeNE, ResizeNW, ResizeSE, ResizeSW, CloseButton, MinimizeButton, MaximizeButton, FullScreenButton
```

`Window.WindowDecorations` is `None | BorderOnly | Full`; `WindowDecorationsTheme` is a
`ControlTheme?`; `ExtendClientAreaTitleBarHeightHint` is a `double` (`-1` = OS default). No
`ExtendClientAreaChromeHints` anywhere in 12.1.4 — the design's §3.1 is correct.

### 1.2 Window facts at runtime (logged by the prototype)

| Fact | Value |
|---|---|
| `WS_*` styles | `0x16CF0000` — `WS_CAPTION`, `WS_SYSMENU`, `WS_THICKFRAME`, `WS_MINIMIZEBOX`, `WS_MAXIMIZEBOX` all set |
| `WindowDecorationMargin` / `OffScreenMargin` | `0,49,0,0` / `0,0,0,0` |
| window vs client | `1216 × 808` vs `1200 × 800` → 8 px frame left/right/bottom, **0 px top** |
| DWM extended frame bounds | inset `(7, 0, 7, 7)`; `DWMWA_WINDOW_CORNER_PREFERENCE` = default (rounded) |
| strip height | `Topbar.Bounds = 0,0,1200,49` — equal to the hint |
| title / icon | `"Frameless Spike"`; `WM_GETICON` big/small non-zero after `Window.Icon` is set |
| screen reader | UIA `Name = "Frameless Spike"`, `ControlType.Window`, `ClassName=MainWindow` |

### 1.3 Hit-test matrix (`WM_NCHITTEST`, real messages)

| Point | Result | Role resolved by Avalonia |
|---|---|---|
| brand / empty strip | `HTCAPTION` | `Border:TitleBar` |
| menu header, menu item | `HTCLIENT` | `Menu:User` |
| strip button | `HTCLIENT` | `Button:User` |
| minimize / maximize / close | `HTMINBUTTON` / `HTMAXBUTTON` / `HTCLOSE` | `Button:MinimizeButton` / `MaximizeButton` / `CloseButton` |
| body | `HTCLIENT` | none |
| edges / corners | `HTLEFT`, `HTRIGHT`, `HTTOP`, `HTBOTTOM`, `HTTOPLEFT`, `HTBOTTOMRIGHT` | — |

All three caption buttons are `IsEnabled=True`, `IsEffectivelyEnabled=True`, `Opacity=1`.

---

## 2. Behaviour results (real Windows, real input)

`main` phase: **16 pass / 4 fail**. `altf4` phase: **1 pass / 0 fail**. `--fix-close` variant:
same suite with `SC_CLOSE` re-enabled (17 pass / 3 fail, §4.2).

| # | Check | Result | Observed |
|---|---|---|---|
| 1 | Window title, `WS_SYSMENU`, `WS_THICKFRAME` | **pass** | `0x16CF0000`, title `Frameless Spike` |
| 2 | Menu opens and activates an item on the **first** click (`User` in the strip) | **pass** | `menu-pointer-pressed pos=128,24` → `menu-pointer-released` → `menu-new-clicked` |
| 3 | Topbar button with `User` receives the click and does not drag | **pass** | `strip-button-clicked`; window rect unchanged |
| 4 | Native drag on the strip | **pass** | moved exactly `+120,+90` px |
| 5 | Double-click maximizes / restores | **pass** | `Maximized` → `Normal` |
| 6 | Single click on the custom maximize button toggles **exactly once** | **pass** | one state change in 1.4 s (`stateChanges=1`); no double toggle (R2 does not occur) |
| 7 | Second click restores | **pass** | `Normal` |
| 8 | Resize from the bottom-right corner | **pass** | `1216×808` → `1296×868` |
| 9 | Minimize button / restore | **pass** | `Minimized` → `Normal` |
| 10 | Custom close button runs `Closing` and exits | **pass** | `button close` → `event closing (cancel=False)` → `event closed` → `app-exit` |
| 11 | Alt+F4 closes the window | **pass** | same closing path; process exits |
| 12 | Snap layouts flyout on hover over the custom maximize button | **pass** | flyout visible on a human-like hover (`evidence/snap/snap-hover-1200ms.png`); `HTMAXBUTTON` precondition verified |
| 13 | Maximized layout | **pass** | strip spans the full width, buttons flush right, 49 px, no gap (`evidence/main/04-maximized-by-button.png`) |
| 14 | `SC_CLOSE` enabled in the system menu | **fail** | state `0x20000003` — greyed **and** disabled (R1 confirmed) |
| 15 | System menu on right-click of the strip | **fail** | no menu window; reproduced at 5 positions across 4 runs, with `WindowFromPoint` confirming the target |
| 16 | System menu Close item closes the window | **fail** | item greyed; even when the menu is forced open, `Up`+`Enter` does nothing |
| 17 | Alt+Space opens the system menu | **fail** | no menu; **also fails on a plain Avalonia window** (see F4) |
| 18 | Taskbar/Alt+Tab identity | **pass** | title + `WM_GETICON` non-zero, no `WS_EX_TOOLWINDOW` |
| 19 | Screen-reader window name and button names | **pass** | UIA: window `Frameless Spike`; buttons `Strip-Aktion[StripButton]`, `Minimieren[ChromeMinimize]`, `Maximieren[ChromeMaximize]`, `Schliessen[ChromeClose]` |

---

## 3. Findings that change the design

### F1 — the no-template decorations theme does **not** suppress Fluent's caption (design §4.2 is wrong)

With the design's primary form (`ControlTheme` with only `DefaultTitleBarHeight`, no `Template`),
the window still drew Fluent's caption buttons over the strip: two sets of glyphs were visible
(ours below, Fluent's 45 × 30 above). A probe theme whose overlay paints a red 12 px bar proved
the theme **is** applied, so the missing `Template` is what leaves Fluent's default content in
place. With the design's own **fallback** form (explicit `WindowDrawnDecorationsContent.Overlay`
containing an empty `Panel`) the duplicate glyphs are gone and only the app's buttons remain.

**Consequence:** S1 must use the explicit empty-overlay template, not the no-template theme.

### F2 — the caption buttons live in the client area and are hit-tested correctly, but the glyphs must be centred explicitly

`Path` content is stretched to the whole 46 × 49 target, so a 10 × 10 geometry renders in the
button's top-left corner (this looked like "invisible buttons" until the bounds were checked).
S1's caption glyphs need `Width`/`Height` + `HorizontalAlignment`/`VerticalAlignment=Center`
(or a `Viewbox`), exactly as the design's "centred" wording implies.

### F3 — R1 is real but narrower than feared

`DisableCloseButton()` runs because the client area is extended: `GetMenuState(SC_CLOSE)` is
`0x20000003` (greyed + disabled).

* Alt+F4 **still closes** the window through the normal `Closing` path (verified twice).
* The custom close button **still closes** the window (it calls `Window.Close()` directly).
* Only the system menu's Close item is dead.

The design's contingency works: after `EnableMenuItem(GetSystemMenu(hwnd,false), SC_CLOSE, MF_ENABLED)`
the state is `0x20000000` (enabled) and the menu item is usable. It must be re-applied whenever
Avalonia re-disables the item (after every state change is the safe choice).

### F4 — Alt+Space never opens the system menu in Avalonia 12.1.4 (pre-existing, not caused by this change)

Alt+Space produced no menu on the frameless window **and** on a plain Avalonia window with native
decorations, while the same injection opens the system menu on a `winver` dialog. This is an
Avalonia-wide behaviour of the version in use, not a regression of variant A — but the design's
coverage table must not claim Alt+Space as a benefit of keeping `Full`.

### F5 — the system menu cannot be opened on the extended strip (regression vs. the native title bar)

Right-click on the strip (brand, empty area, the native left/top resize border, the top-left
corner) never opened `#32768`, in four runs, with `WindowFromPoint` verifying that the target was
the spike window and `WM_NCHITTEST` returning `HTCAPTION`. The A/B control is conclusive: the same
probe on a plain Avalonia window opens the system menu (`Wiederherstellen`, `Verschieben`,
`Größe ändern`, `Minimieren`, `Maximieren`, `Schließen Alt+F4`) and its Close item closes the
window (`evidence/sysmenu-plain2/sysmenu-open.png`).

So with variant A the system menu is reachable **neither** by right-click **nor** by Alt+Space:
`Move`/`Size` and the keyboard path to them are lost. `EnableMenuItem` fixes the Close item but
does not bring the menu back.

### F6 — everything else in the design's behaviour table holds

Native drag, double-click maximize/restore, all resize edges/corners, `HTMAXBUTTON` snap-layout
flyout, single-click maximize with the NC redirect (R2 does not happen), custom minimize/restore/
close, taskbar identity, UIA names, and a maximized strip that needs no manual padding.

---

## 4. Not verified in this spike (and why)

| Item | Why |
|---|---|
| DWM minimize/maximize/close **animation frames** | the screenshot loop costs ~185 ms per frame (slower than the ~200 ms animation), so no intermediate frame could be captured; system animation is enabled (`SPI_GETCLIENTAREAANIMATION=True`) and `WS_CAPTION`/`WS_SYSMENU` are present, which is the documented precondition. Re-check visually in S3. |
| Drag-to-edge Aero Snap, drag-from-maximized-to-restore | not exercised by the driver; the strip is `HTCAPTION`, which is the same mechanism as the native caption, but this is an assumption, not a measurement |
| 150 %/200 % DPI and multi-monitor | machine runs a single 100 % monitor; S3 must do this |
| Windows 10 | not available here |
| Narrator speech output | UIA tree verified instead (window name + all button names/IDs); Narrator itself was not driven |
| Touch | no touch device |
| High contrast, live | system HC is **off** (`HighContrastOn=False`, Avalonia `ContrastPreference=NoPreference`) and it was deliberately not toggled system-wide. Source check: the prototype's strip uses fixed colours (`#f5f7f9`, `#5f6b7d`, `#c42b1c`) with no `ColorValuesChanged` handling, so a custom strip cannot inherit the native HC palette. The design's S4 task stands. |
| Dirty-project `Closing` prompt | product code not touched; the spike only proves that `Closing` runs from the custom button and from Alt+F4 |

---

## 5. Prototype defects found (not Avalonia defects)

1. Glyph centring (F2).
2. The no-template theme (F1) — a design defect, reproduced deliberately.
3. Driver-side: click coordinates must be recomputed after every window move/resize/maximize;
   the first version cached the origin and produced false failures.

---

## 6. Gate assessment against the design's S0

| Gate item | Result |
|---|---|
| Alt+F4 / system-menu Close broken beyond a cheap fix? | Alt+F4 **works**; the Close item is greyed but `EnableMenuItem` re-enables it in one call (verified). **Gate passed with the workaround.** |
| Client `HTMAXBUTTON` button double-toggles? | No — exactly one toggle per click. **Gate passed.** |
| Snap layouts, drag, double-click, resize, buttons | all verified working |
| System menu reachable | **not** on the strip, and not via Alt+Space (Avalonia-wide). Not covered by the design; see §7. |

---

## 7. Recommendation

**Proceed with variant A, with three mandatory corrections:**

1. **Decorations theme:** use the explicit empty-overlay `WindowDrawnDecorationsTemplate`
   (design §4.2 fallback). Delete the no-template form from the design; it does not work.
2. **System menu Close:** keep the `EnableMenuItem(SC_CLOSE, MF_ENABLED)` workaround (via
   `TryGetPlatformHandle()`), re-applied after state changes, so Alt+F4's menu counterpart and
   the menu item are consistent. Verified: `0x20000003 → 0x20000000`.
3. **System menu access:** because Windows no longer provides the system menu on the extended
   strip and Alt+Space is broken in Avalonia 12.1.4 anyway, S1 must ship a small replacement on
   right-click of the strip (a context menu with Restore/Move/Size/Minimize/Maximize/Close, or at
   minimum Restore/Minimize/Maximize/Close) **or** the team must explicitly accept that
   `Move`/`Size` are unavailable. Do not document the system menu as "kept by `Full`".

With those three, the design's promise ("native drag, double-click, snap, snap layouts, resize,
animations, taskbar and screen-reader name, custom 46 × 49 buttons") is met.

**Fallback:** if a native system menu on the caption strip is a hard requirement for this cycle,
keep the native title bar (variant E). Variant B (managed decorations overlay) does not fix any of
F1/F4/F5 — it is still an extended client area — so it is not a way around them.

Remaining risks after this spike: DWM animation appearance (visual check), Aero Snap drag,
150/200 % DPI and multi-monitor, Windows 10 support, high contrast, Narrator, and the extra
topbar context menu (a new, small piece of UI that needs its own accessibility pass).
