# BusChord frameless shell: design and implementation plan

> **Investigation/design only.** No product code, `App.axaml`, `WorkbenchWindow`, dialogs, tests,
> CI, packages, or existing reports were changed. This branch contains this report and one static
> wireframe. Nothing was pushed.
>
> - Base: `98f8bdf8d3e2e1965c59fb96d7f198c213a5133d` ("Record catalog load findings in the plan")
> - Branch: `agents/ui/frameless-design`
> - Worktree: `C:\projects\ai_work\agent-wt\frameless-design`
> - Wireframe: [ui-frameless/topbar-wireframe.svg](ui-frameless/topbar-wireframe.svg)
> - Package set: Avalonia `12.1.4`, `net9.0`, Windows-first (see `src/Knx.App/Knx.App.csproj`)
>
> **Verdict: proceed, but only through the real-Windows gate in stage S0.** Variant A below gives
> the shell a genuinely frameless look while keeping Windows drag, double-click, Aero Snap, snap
> layouts, the system menu, resize borders, DWM animations, the taskbar title/icon and the
> screen-reader window name. It depends on two behaviours that cannot be proven from an offscreen
> render: Alt+F4 / system-menu Close, and the single-click behaviour of a client-drawn maximize
> button with the Win32 `HTMAXBUTTON` role. If either fails and cannot be fixed cheaply, do not
> ship a frameless shell in this cycle.

---

## 1. What the user asked for, and what "frameless" should mean here

Remove the OS-drawn title bar; keep the window buttons, but integrate them into the existing V4 top
strip rather than floating a fake Windows caption group over the content. The shell must keep
resize, drag, minimize, maximize/restore, close, the project/window title, keyboard access and
screen-reader access.

"Frameless" therefore does **not** mean `SystemDecorations=None` plus hand-rolled drag/resize code.
That variant throws away native caption semantics (`HTMAXBUTTON` snap layouts, double-click,
system menu, DWM animations) and has to reimplement them. The design keeps the native *window
frame* and native *caption behaviour*, and removes only the native *drawing* of the caption.

---

## 2. Current shell and chrome

### 2.1 Shell geometry

`src/Knx.App/WorkbenchWindow.axaml:3-5,38,54,137-141`:

| Part | Current value | Notes |
|---|---|---|
| Window | 1440 × 900, `MinWidth=900`, `MinHeight=480` | `Title="BusChord"` in XAML, replaced at runtime |
| Topbar | row height **49**, `WbPaper`, 1 px `WbLine` bottom rule, 12 px horizontal inset | brand / menu / search / OFFLINE / Programming |
| Body | rail **47**, navigator **272** (210 below 1100), splitter **5**, document area `*`, inspector **350** (overlay at narrow) | `WorkbenchTheme.axaml:24-34` |
| Status bar | **27** | `WbStatusbar` |
| Icon | `Assets/brand/buschord-house-transparent-tile.ico` | taskbar/Alt+Tab |
| Window title | `WorkbenchWindow.axaml.cs:393` `Title = ViewModel.Title` | `"BusChord - <project> *"` (`MainWindowViewModel.cs:58`) |

Topbar composition (`WorkbenchWindow.axaml:39-52`), left to right:

1. 12 px inset, 28 × 28 brand mark, 8 px gap, `BUSCHORD` (12 px bold), 16 px gap.
2. `Menu` (`MainMenu`, four items built in `WorkbenchWindow.Commands.cs:17-73`: Projekt,
   Bearbeiten, Ansicht, Werkzeuge).
3. Project search button, `MinWidth=220`, centred in the remaining star column
   (`Content="{loc:Wb SearchProjectButton}"`, handler `OnPalette`, Ctrl+K).
4. `OFFLINE` (10 px, `WbWarning`), 8 px gap, Programming primary button (30 px high).
5. 12 px right inset.

The 49 px height is duplicated as the literal row height and `WbTopbarHeight=49`
(`WorkbenchTheme.axaml:24`). The new design must make that one value authoritative.

### 2.2 Window styles

- Global `Window` style: Segoe UI 12.5, `EtsChrome`, `EtsText` (`App.axaml:142-147`).
- `WorkbenchWindow` overrides: Inter 12, `WbBackground`, `WbText`.
- Dialogs keep the default native decorations: `SettingsWindow.axaml:5-7`,
  `NewProjectWindow.axaml:5-6`, `CompareWindow.axaml`, `ReplaceProductWindow.axaml`,
  `ReportWindow.axaml`, the code-only `ConfirmDialog` (`ConfirmDialog.cs:33-38`) and every
  ad-hoc `CreateDialog` window (`WorkbenchWindow.Commands.cs:75-81`).
- Floated tools are plain native `Window`s (`WorkbenchWindow.axaml.cs:2270-2281`).
- The splash is the only borderless window today: `SystemDecorations="None"`, transparent,
  topmost, 380 × 420 (`SplashWindow.axaml:4-6`). `SystemDecorations` is obsolete in 12.1.4 but
  still works.

### 2.3 What the OS title bar provides today

| Value | Who relies on it | Covered by the recommended design? |
|---|---|---|
| Drag / move | everyone | yes, native `HTCAPTION` on the strip |
| Double-click maximize | everyone | yes, native |
| Aero Snap by dragging to an edge | everyone | yes, native |
| Windows 11 snap-layout flyout on maximize hover | everyone | yes, via `HTMAXBUTTON` on our button (verify) |
| System menu: right-click, Alt+Space, Move/Size | keyboard and accessibility users | yes with `WindowDecorations=Full` (Close item must be verified) |
| Minimize / maximize / restore / close | everyone | yes, custom buttons in the strip |
| Resize borders and corners | everyone | yes, native `WS_THICKFRAME` |
| DWM shadow, rounded corners, min/max/close animations | perceived quality | yes with `Full` + extended client area |
| Taskbar title, icon, thumbnail | everyone | yes, keep `Window.Title` / `Window.Icon` |
| Screen-reader window name/role | Narrator/UIA | yes, `Window.Title` unchanged |
| High contrast | HC users | **not automatic**; the custom strip uses the app palette (see S4) |
| Alt+Tab preview | everyone | yes, live window content |
| Alt+F4 | keyboard users | **must be verified** (see risk R1) |

---

## 3. Avalonia 12.1.4: what is actually available

### 3.1 Verified API surface

Checked in the local package XML docs
(`C:\Users\julia\.nuget\packages\avalonia\12.1.4\ref\net8.0\Avalonia.Controls.xml`) and in the
Avalonia `12.1.4` source for the exact tag.

| API | Status in 12.1.4 | Use here |
|---|---|---|
| `Window.WindowDecorations` (`Full` default, `BorderOnly`, `None`) | present | **`Full`** |
| `Window.SystemDecorations` | present but `[Obsolete("Use WindowDecorations instead.")]` | not used in new code |
| `Window.ExtendClientAreaToDecorationsHint` | present | **`True`** |
| `Window.ExtendClientAreaTitleBarHeightHint` | present, `-1` = OS default | **`49`** |
| `Window.WindowDecorationsTheme` (`ControlTheme?`) | present | custom theme that draws nothing |
| `Window.WindowDecorationMargin` | present, read-only | informational; do not use as padding |
| `Window.OffScreenMargin` | present | Win32 returns zero |
| `ExtendClientAreaChromeHints` | **removed in 12** (not in the 12.1.4 assembly/XML) | do not use |
| `WindowDecorationProperties.ElementRole` (`Avalonia.Controls.Chrome`) | present | marks drag region / interactive controls / caption buttons |
| `WindowDecorationsElementRole` | `None`, `DecorationsElement`, `User`, `TitleBar`, `ResizeN/S/E/W/NE/NW/SE/SW`, `CloseButton`, `MinimizeButton`, `MaximizeButton`, `FullScreenButton` | as listed |
| `WindowDrawnDecorations`, `WindowDrawnDecorationsContent`, `WindowDrawnDecorationsTemplate` | present; Fluent supplies a default theme with its own 30 px title text and 45 × 30 caption buttons | suppress with a custom theme |
| `Window.BeginMoveDrag`, `BeginResizeDrag` | present | **not needed**; using them would lose native caption semantics |
| `TopLevel.TryGetPlatformHandle()` | present | only for the contingency Win32 close-menu fix |
| `PlatformColorValues.ContrastPreference` (`ColorContrastPreference.High`) and `ColorValuesChanged` | present | high-contrast detection in S4; **no** automatic HC palette for the app's `Wb*` tokens |

Key rule from the docs and upstream maintainers: **`ElementRole` only has an effect when
`ExtendClientAreaToDecorationsHint` is `true`** (Avalonia issue #21202, MrJul). We set it.

### 3.2 Win32 behaviour in 12.1.4 (source-verified)

`src/Windows/Avalonia.Win32/WindowImpl.cs`:

- `WindowDecorations.Full` sets `WS_BORDER | WS_SYSMENU | WS_CAPTION`; `BorderOnly` sets
  `WS_BORDER` only; both keep `WS_MINIMIZEBOX`, `WS_MAXIMIZEBOX`, and `WS_THICKFRAME` when
  `CanResize`. `None` drops the thick frame, so a `None` window has no native resize border.
- `ExtendClientAreaToDecorationsHint=true` keeps those styles, calls
  `DwmExtendFrameIntoClientArea`, sets `NeedsManagedDecorations=true`, and requests only the
  `TitleBar` decoration part on Win32. With `Full`, the extended top margin is
  `ExtendClientAreaTitleBarHeightHint`; with `BorderOnly`, the hint is ignored and the margin is
  only the thin top border.
- `WM_NCHITTEST` (`WindowImpl.CustomCaptionProc.cs`): the extended strip is `HTCAPTION`; then
  `HitTestVisual` walks the visual under the pointer and maps the nearest
  `WindowDecorationProperties.ElementRole`:
  `TitleBar → HTCAPTION`, `User`/`DecorationsElement → HTCLIENT`,
  `MinimizeButton → HTMINBUTTON`, `MaximizeButton → HTMAXBUTTON`, `CloseButton → HTCLOSE`,
  resize roles to `HTTOP`/`HTBOTTOM`/….
  In maximized state the automatic caption strip is 49 minus the top frame border (≈41 px at
  100 %), because `_extendedMargins.Top` is reduced by the border thickness; the bottom few pixels
  of the 49 px strip then rely on the `TitleBar` role. Keep the role even though the strip is
  caption by default.
- `PresentationSource.GetChromeRoleFromVisual`
  (`src/Avalonia.Controls/PresentationSource/PresentationSource.cs:138-158`) walks **up the
  visual tree** to the nearest non-`None` role. Therefore an interactive child with no role inside
  a `TitleBar` region resolves to `TitleBar` and the click becomes a window drag. Every button,
  menu, text field or combo box in the strip needs `User`.
- `WM_NCLBUTTONDOWN`/`WM_NCLBUTTONUP` over `HTMINBUTTON`/`HTMAXBUTTON`/`HTCLOSE` are redirected to
  managed pointer events when the element handles them, so ordinary Avalonia `Button.Click`
  handlers work on the custom caption buttons.
- `DisableCloseButton()` calls `EnableMenuItem(GetSystemMenu(hwnd), SC_CLOSE, MF_GRAYED)` whenever
  the client area is extended. This is the reason Alt+F4 and the system-menu Close item must be
  tested rather than assumed.
- On Win32, `OffScreenMargin` is zero and `WindowDecorationMargin` is informational in extended
  mode; Avalonia does **not** inset the content. Our topbar must start at y=0 itself.

Upstream context for the chosen combination:

- Avalonia issue **#21212**: `Full` + `ExtendClientAreaToDecorationsHint=True` + a custom
  `WindowDrawnDecorations` theme with an empty overlay is the v12 replacement for the old
  `ExtendClientAreaChromeHints=NoChrome`; it preserves DWM animations, removes the duplicated OS
  title and caption glyph, and removes the snap flyout from the native region. Animations were
  fixed in 12.1.0; 12.1.4 includes the Win32 animation fix (#21498).
- Issue **#21167**: a menu in the extended strip was not clickable until it was marked
  `DecorationsElement`; the OS title text also needed to be suppressed.
- Issue **#21202**: `BorderOnly` + extended is **not** equivalent: no `WS_SYSMENU`, no DWM
  min/max/close animations.
- Issue **#21318** (macOS, closed): first click on a `TitleBar` region could maximize. Windows
  behaviour must be checked in S0.

### 3.3 Options compared

| Option | Properties | Windows result | Verdict |
|---|---|---|---|
| **A. Full + extended + empty drawn theme** | `WindowDecorations="Full"`, `ExtendClientAreaToDecorationsHint="True"`, `ExtendClientAreaTitleBarHeightHint="49"`, custom `WindowDecorationsTheme` with no template | Native caption bits and DWM animations kept; no OS title/glyph; roles drive caption hit-testing; native frame/shadow/resize/snap/system menu | **recommended** |
| B. BorderOnly + extended | `WindowDecorations="BorderOnly"` | No `WS_SYSMENU`, no Alt+Space, no DWM min/max/close animations; native resize still works; no custom theme needed | fallback only if A's theme trick regresses |
| C. None + extended | `WindowDecorations="None"` | No thick frame → no native resize border | reject |
| D. Manual drag/resize | `BeginMoveDrag`, `BeginResizeDrag`, custom grips, `WM_NCHITTEST` | Reimplements drag and resize; loses double-click/snap/system menu unless reimplemented in Win32 | reject |
| E. Keep native title bar | current | All native, but the user request is unmet | abort path |

`BorderOnly` is tempting because it removes the drawn title with no custom theme, but it fails the
"honest about Windows" requirement: no animations and no system menu. Do not ship it unless the
team explicitly accepts both losses.

---

## 4. Recommended design: Variant A

### 4.1 Window settings

```xml
<Window x:Class="Knx.App.WorkbenchWindow"
        ...
        Title="BusChord"
        Icon="/Assets/brand/buschord-house-transparent-tile.ico"
        WindowDecorations="Full"
        ExtendClientAreaToDecorationsHint="True"
        ExtendClientAreaTitleBarHeightHint="49"
        WindowDecorationsTheme="{DynamicResource WbWindowDecorationsTheme}">
```

- `Title` stays as today: set from `ViewModel.Title` in `RefreshWorkspace`
  (`WorkbenchWindow.axaml.cs:393`). It is the taskbar/Alt+Tab name and the screen-reader window
  name; do **not** blank it to hide the native title (that would remove the taskbar name too).
- The window-chrome properties are Windows-only in effect. If the app is ever run on
  Linux/macOS, set them from code behind `OperatingSystem.IsWindows()` and leave native
  decorations elsewhere; do not claim cross-platform frameless support in this change.
- Do not enable Mica/Acrylic/transparency. It complicates the frame, hit-testing and animations
  and is not part of the request.

### 4.2 Suppressing the drawn decorations

`WorkbenchTheme.axaml` (application-level style resources, so `Wb*` tokens are visible to the
decoration layer):

```xml
<ControlTheme x:Key="WbWindowDecorationsTheme"
              TargetType="chrome:WindowDrawnDecorations">
  <!-- No Template: the native frame remains, Avalonia draws no title text and no caption buttons. -->
  <Setter Property="DefaultTitleBarHeight" Value="49" />
</ControlTheme>
```

with `xmlns:chrome="using:Avalonia.Controls.Chrome"` on the resource dictionary.

If the `DynamicResource` has not resolved by the time the window is first shown, assign the theme
in the constructor after `InitializeComponent`:
`WindowDecorationsTheme = (ControlTheme)Application.Current!.FindResource("WbWindowDecorationsTheme")!;`
and assert it is non-null in the S1 test. Do not silently fall back to Fluent's decorations theme,
which would draw the duplicate title and caption buttons.

Why this works: `WindowDecorationsTheme` replaces Fluent's `WindowDrawnDecorations` theme. The
decoration object still exists and still computes `TitleBarHeight=49` (so the native caption strip
and DWM margins stay correct), but its `Template` is null, so no overlay content is created. Fluent's
default theme is what draws the 30 px title and the 45 × 30 caption buttons; replacing it removes
both. Issue #21212 confirms the empty-theme approach; the no-template form above is the smallest
expression of it and must be run-checked (see the fallback).

Fallback if a no-template theme is rejected by a future Avalonia version: give the theme an
explicit empty overlay (same issue #21212):

```xml
<Setter Property="Template">
  <WindowDrawnDecorationsTemplate>
    <WindowDrawnDecorationsContent>
      <WindowDrawnDecorationsContent.Overlay>
        <Panel Height="{TemplateBinding TitleBarHeight}" IsHitTestVisible="False" />
      </WindowDrawnDecorationsContent.Overlay>
    </WindowDrawnDecorationsContent>
  </WindowDrawnDecorationsTemplate>
</Setter>
```

The integrator must compile-check the exact template element names; the empty-overlay variant is
only a fallback, not the primary.

### 4.3 Top strip layout

Static reference: [ui-frameless/topbar-wireframe.svg](ui-frameless/topbar-wireframe.svg)
(light, dark, states, role map, 900 px layout).

```
0                                                                                         1440
┌───────────────────────────────────────────────────────────────────────────────────────────┐ 49
│ 12 [28 logo] 8 BUSCHORD │ 10 Demo-Projekt * 16 │ Projekt Bearbeiten Ansicht Werkzeuge     │
│                            ⋯ star ⋯ [  Projekt durchsuchen        Ctrl K  ] ⋯ star ⋯      │
│                                                    OFFLINE 8 [Programmieren] 10 [–][□][×] │
└───────────────────────────────────────────────────────────────────────────────────────────┘
   ↑ drag (TitleBar)         ↑ User          ↑ User                   ↑ User      ↑ roles
```

| Segment | Spec | Notes |
|---|---|---|
| Left inset | 12 px | unchanged |
| Brand | 28 × 28 mark + 8 px + `BUSCHORD` 12 px bold + 16 px | unchanged |
| Divider | 1 × 21 px `WbLine`, ~10 px after the wordmark | new, separates brand from document title |
| Document title | 11 px `WbMuted`, max 180 px, `TextTrimming=CharacterEllipsis`, includes the dirty `*` | new; hidden below 1280 px |
| Menus | same four items, 12 px, 30 px hit height, same order | preserve |
| Search | 260 × 30, `WbBackground` fill, radius 5, magnifier + hint + `Ctrl K`; centred in the star cell | current quiet 220 min, same handler `OnPalette` |
| OFFLINE | 10 px `WbWarning` | unchanged |
| Programming | 112 × 30 primary, `WbAccent` / `WbOnAccent` | unchanged |
| Caption cluster | 3 × 46 × 49, flush right, 0 radius, no outer right margin | new |
| Bottom rule | 1 px `WbLine` under the content columns, not under the caption cluster | new detail |

The new caption targets are 46 × 49, and the compact search icon is 36 × 32. The existing V4
controls (menu items, search, Programming) stay at their current 30 px height; changing them only
in the topbar would misalign the shell. If "every topbar target ≥ 32 px" is a hard requirement, bump
the shared `WbButtonHeight`/menu padding in a separate V4 token change and re-screenshot the whole
shell, not just the strip.

The caption cluster is 138 px, so the star cell shrinks by 138 px and the search moves ~69 px left
relative to the old layout. It stays centred between the menu and the right-hand actions, which is
the existing composition rule; do not try to keep it centred on the window with an overlay.

The topbar height stays **49** in normal, maximized and restored states. Do not let it grow with
localization; if a menu label grows, it ellipsizes or the responsive rules kick in, never a second
row. The native caption strip height is derived from the same 49 (the S1 test asserts equality).

### 4.4 Structure and roles

```xml
<Border x:Name="Topbar" Background="{DynamicResource WbPaper}"
        chrome:WindowDecorationProperties.ElementRole="TitleBar">
  <Grid ColumnDefinitions="*,Auto" Margin="12,0,0,0">
    <!-- 1 px rule under the content columns only; caption blocks stay full height. -->
    <Border x:Name="TopbarRule"
            BorderBrush="{DynamicResource WbLine}" BorderThickness="0,0,0,1">
      <Grid x:Name="TopbarContent" ColumnDefinitions="Auto,Auto,*,Auto">
        <!-- brand, title, Menu, search, OFFLINE, Programming -->
      </Grid>
    </Border>

    <StackPanel x:Name="WindowButtons" Grid.Column="1" Orientation="Horizontal">
      <Button x:Name="ChromeMinimize" Classes="wb-caption"
              chrome:WindowDecorationProperties.ElementRole="MinimizeButton"
              AutomationProperties.AutomationId="ChromeMinimize" ... />
      <Button x:Name="ChromeMaximize" Classes="wb-caption"
              chrome:WindowDecorationProperties.ElementRole="MaximizeButton"
              AutomationProperties.AutomationId="ChromeMaximize" ... />
      <Button x:Name="ChromeClose" Classes="wb-caption wb-caption-close"
              chrome:WindowDecorationProperties.ElementRole="CloseButton"
              AutomationProperties.AutomationId="ChromeClose" ... />
    </StackPanel>
  </Grid>
</Border>
```

Add `xmlns:chrome="using:Avalonia.Controls.Chrome"` to `WorkbenchWindow.axaml`. The `Grid` is the
single top-level layout; do not add a second root or an overlay above the content.

The generic interactive role applies only to the content grid, so it cannot override the caption
buttons' local roles:

```xml
<Style Selector="Grid#TopbarContent Button, Grid#TopbarContent Menu">
  <Setter Property="chrome:WindowDecorationProperties.ElementRole" Value="User" />
</Style>
```

Add the same setter for any future `TextBox`, `ComboBox`, `CheckBox` or `Hyperlink` placed in the
strip. A comment next to the style should say why.

### 4.5 Window-control buttons

They stay in the top-right of the strip rather than moving into the rail or status bar: that is
where Windows users look, it is the closest target to the screen corner, and it does not turn
minimize/close into app commands. The integration is visual (same 49 px strip, same paper, V4
tokens), not a floating caption group over the content.

- Target: **46 × 49** each, 138 px total, flush to the window edge (the close block ends at x=1440).
  No floating capsule, no rounded group, no gap to the frame.
- Glyphs: 10 × 10, 1.2 px square stroke, centred:
  - minimize: `M0 5 H10`
  - maximize: `M0 0 H10 V10 H0 Z`
  - restore: two offset 8 × 8 squares
  - close: `M0 0 L10 10 M10 0 L0 10`
- Use `Path` rather than `ConceptIcon`: caption glyphs are a platform idiom at 10 px, not the
  17 px V4 concept set. Do not enlarge them to 17 px.
- Default glyph `WbMuted`; hover `WbText` + `WbHover` background; pressed `WbAccent` +
  `WbTint`; disabled `WbDisabledText`, no hover, opacity 1 (V4 rule).
- Close: default same as the others; hover `#c42b1c` with a `#ffffff` glyph; pressed `#a32416`.
  This is the one native convention worth keeping and it is still a full-height block in the
  strip, not a floating button.
- Focus-visible: 2 px inset `WbAccent` ring for minimize/maximize, 2 px inset
  `WbChromeCloseForeground` for the close button (white on the red hover). The ring is inside the
  46 × 49 target.
- `IsEnabled`: minimize follows `CanMinimize`; maximize follows `CanMaximize`; close is always
  enabled unless the whole window is disabled by a modal dialog (Avalonia disables the owner
  window automatically).
- On `WindowState` change: swap the maximize glyph to restore, set the automation name to
  "Wiederherstellen"/"Restore", and back. Never leave the name "Maximize" while the window is
  maximized.
- Handlers: `WindowState = Minimized`; toggle `Maximized`/`Normal`; `Close()`. The close handler
  must go through `Close()` so the existing `Closing` flow
  (`WorkbenchWindow.Commands.cs:212` `OnClosing`) and the unsaved-project prompt still run. Do not
  call `Environment.Exit`.

### 4.6 Tokens (light / dark)

Add to `WorkbenchTheme.axaml` theme dictionaries; reuse existing tokens where possible.

| Token | Light | Dark | Use |
|---|---|---|---|
| `WbChromeButtonForeground` | `#5f6b7d` (`WbMuted`) | `#b0bdce` (`WbMuted`) | default glyph |
| `WbChromeButtonHover` | `#f0f4f5` (`WbHover`) | `#2b3745` (`WbHover`) | hover fill |
| `WbChromeButtonPressed` | `#e4f2f1` (`WbTint`) | `#233f43` (`WbTint`) | pressed fill |
| `WbChromeButtonHoverForeground` | `#202c3c` (`WbText`) | `#e5ebf3` (`WbText`) | hover glyph |
| `WbChromeCloseHover` | `#c42b1c` | `#c42b1c` | close hover |
| `WbChromeClosePressed` | `#a32416` | `#a32416` | close pressed |
| `WbChromeCloseForeground` | `#ffffff` | `#ffffff` | close glyph |
| `WbChromeDivider` | `WbLine` `#e1e5eb` | `WbLine` `#3c4858` | brand/title divider |

Contrast (graphical objects need 3:1): `#5f6b7d` on `#ffffff` ≈ 5.4:1; `#b0bdce` on `#202833`
≈ 7.8:1; white on `#c42b1c` ≈ 5.6:1; `WbAccent` `#087e81` on white ≈ 4.9:1. Re-measure with the
real button theme; the V4 focus ring token is already 2 px.

### 4.7 Focus order and names

Visual order in the strip, with the caption cluster last:

1. `MainMenu` (one tab stop; arrow keys within).
2. Search (`Ctrl+K` also works from anywhere).
3. Programming.
4. Minimize.
5. Maximize / Restore.
6. Close.
7. Existing rail / navigator / editor order (unchanged).

Skip the brand image, wordmark and OFFLINE status; they are not interactive. Keep
`KeyboardNavigation.TabNavigation="Continue"`; no focus trap in the topbar.

Names (localized through `WorkbenchStrings`, English fallback shown):

| Control | Name | AutomationId |
|---|---|---|
| Minimize | Minimize / Minimieren | `ChromeMinimize` |
| Maximize | Maximize / Maximieren | `ChromeMaximize` |
| Restore | Restore / Wiederherstellen | `ChromeMaximize` |
| Close | Close / Schließen | `ChromeClose` |
| Search | existing search string | `TopbarSearch` |
| Document title | static text; no duplicate window name | `TopbarTitle` |

Set `AutomationProperties.IsControlElement="False"` on the brand image and on the `Topbar` drag
Border. `Window.Title` already supplies the window name; do not add a second "title bar" control
to the automation tree.

### 4.8 Responsive rules

| Width | Document title | Search | Result |
|---|---|---|---|
| ≥ 1280 | visible, max 180 px | full 260 × 30 | 1440 screenshot target |
| 1040–1279 | hidden | full 260 × 30 | menus + search + actions + 138 px controls |
| 900–1039 | hidden | 36 × 32 icon button, tooltip + name, Ctrl+K | MinWidth 900 target |

The three breakpoints must be verified with the existing `--screen=demo` (1440), `demo-small`
(1024) and `demo-tiny` (900) captures. If the measured German menu widths overflow at 1024,
raise the icon-only breakpoint to the measured value; do not shrink the caption targets below
46 × 49.

---

## 5. Fallback variant B (only if A's empty-theme path fails)

Keep `WindowDecorations=Full` + extended, but let `WindowDrawnDecorations` own the caption buttons:
a custom theme with `PART_MinimizeButton`, `PART_MaximizeButton`, `PART_CloseButton`, styled as
46 × 49 V4 blocks, and reserve 138 px in the topbar. Avalonia then handles clicks, enabled state,
glyph swap and automation names, and the buttons must carry the matching
`MinimizeButton`/`MaximizeButton`/`CloseButton` roles in the theme (the Fluent theme does this; a
copied theme must keep it, or the snap flyout and NC close behaviour are lost).

Trade-offs: the buttons live in the overlay above the client area, not in the client grid, so the
spacer must track; automation names are hardcoded English in `WindowDrawnDecorations`; tab order is
not the client order. It is a lower-code fallback, not the preferred design.

Abort path: keep the native title bar. Do not ship `BorderOnly` merely to get frameless; losing
DWM animations and the system menu is a worse regression than keeping the title bar.

---

## 6. Dialogs and other windows

**Decision: only `WorkbenchWindow` gets custom chrome. Every dialog and secondary window keeps its
native title bar.**

| Window | Current | Decision | Why |
|---|---|---|---|
| `WorkbenchWindow` | native | custom chrome | the user request |
| `SettingsWindow` (720 × 620, min 560 × 480) | native | native | long form, system close is the expected escape |
| `NewProjectWindow` (560 × 440, min 460 × 380) | native | native | modal, create/cancel |
| `CompareWindow` (900 × 640) | native | native | short-lived, own title |
| `ReplaceProductWindow` (720 × 560, min 560 × 420) | native | native | destructive, needs an obvious close |
| `ReportWindow` (900 × 620, min 600 × 360) | native | native | read-only, own title |
| `ConfirmDialog` (440, `SizeToContent.Height`) | native, `ShowInTaskbar=false`, `CenterOwner`, Escape/Cancel | native | **never** make a destructive dialog harder to close |
| Ad-hoc `CreateDialog` (prompt/move/value/…) | native | native | same rule |
| Floated tool windows | native | native | independent windows with their own title |
| `SplashWindow` | `SystemDecorations=None`, topmost | keep | already borderless; only migrate the obsolete property opportunistically |

The visual inconsistency (frameless shell, native dialogs) is accepted. A shared dialog chrome
would be a second implementation with no user request; add it only if users complain.

Modal behaviour: when a dialog is open, Avalonia disables the owner window, so the shell's custom
caption buttons are disabled with the rest of the window. The native dialog remains draggable and
closable. Test the dirty-project close flow from the custom X and from Alt+F4; both must reach the
same `Closing` prompt and Cancel must leave the window open.

---

## 7. Behaviour coverage

| Behaviour | Mechanism | Code needed | Verification |
|---|---|---|---|
| Single-click drag | `TitleBar` role → `HTCAPTION`; `Full` + hint 49 makes the strip caption by default | none | S0/S3 manual |
| Double-click maximize | native `HTCAPTION` | none | S0/S3 manual |
| Aero Snap drag | native | none | S0/S3 manual |
| Snap layouts flyout | `MaximizeButton` role → `HTMAXBUTTON` | none | S0 manual (hover) |
| Right-click system menu | `HTCAPTION` + `WS_SYSMENU` (`Full`) | none | S0 manual |
| Alt+Space | `WS_SYSMENU` | none | S0 manual |
| Minimize / maximize / restore / close | client buttons with roles; NC input redirect | 3 handlers, glyph/name update | S1 + S3 |
| Resize borders/corners | native `WS_THICKFRAME` | none | S0/S3 manual |
| Maximized padding | DWM keeps the client at the work area; do not add manual padding | none | S3 at 100/150 % |
| Drag from maximized to restore | native `HTCAPTION` | none | S3 manual |
| Taskbar title/icon/thumbnail | `Window.Title` / `Window.Icon` | keep | S3 manual |
| Screen-reader window name | `Window.Title` | keep | S4 Narrator |
| Button names/states | `AutomationProperties.Name` + state update | names + swap | S1 headless + S4 Narrator |
| Keyboard access to buttons | Tab/Shift+Tab, Enter/Space | focusable Buttons | S4 keyboard-only |
| Alt+F4 | native close path | **verify**; contingency `EnableMenuItem(SC_CLOSE)` via `TryGetPlatformHandle` | S0 |
| Touch | native caption + 46 × 49 targets | none | S3 touch device |
| Full-screen | **not in scope**; do not set `WindowState.FullScreen` | none | code review |
| Focus layout | existing `Workspace.FocusLayout` only hides panes; window stays normal | none | S2 existing tests |
| Modal dialogs | Avalonia disables the owner | none | S3/S5 |
| High contrast | `ContrastPreference.High` + chrome-only HC class; no automatic `Wb*` adaptation | S4 task | S4 manual |

Full-screen note: with `WindowState.FullScreen`, `ComputeDecorationParts` removes the title bar and
Avalonia's default theme shows a hover-reveal popover. Our empty decorations theme has no popover,
so F11-style full-screen would strand the user without window buttons. Either do not expose OS
fullscreen in this change (recommended) or add a `FullscreenPopover` with minimize/restore/close
before enabling it.

---

## 8. Staged implementation

Ordered by risk. Each stage is a separately reviewable commit on an implementation branch, not on
this report branch. Do not start S1 before S0 passes.

### S0 — Real-Windows gate (no merge)

Goal: prove the platform contract before touching the shell.

1. Temporary copy of `WorkbenchWindow` (or a tiny throwaway Avalonia window, not committed) with
   the exact settings from §4.1 and the empty decorations theme from §4.2.
2. Run on Windows 11 (and Windows 10 if it is still supported) at 100 % and 150 %.
3. Check, with real screen captures (not `RenderTargetBitmap`):
   - no OS title text, no native caption glyphs, no 1 px native caption line;
   - DWM min/max/restore/close animations;
   - drag on the strip, double-click, drag-to-edge snap, drag from maximized to restore;
   - snap-layout flyout on hover over the custom maximize button;
   - resize from every edge/corner; shadow/rounded corners;
   - `Alt+F4` and `Alt+Space`; right-click system menu; whether Close is greyed;
   - one click on the maximize button toggles exactly once;
   - a menu in the strip opens on the first click (not a drag);
   - Narrator reads the window name and the button names.

**Gate:** if Alt+F4/system-menu Close or the single-click maximize is broken and cannot be fixed
with a small, documented workaround, stop and keep the native title bar. Do not merge S1.

Evidence: a short `analysis/` note with the exact OS build, scaling and screenshots, plus the
pass/fail of each line above.

### S1 — Chrome plumbing (risk: highest implementation risk)

Files: `WorkbenchWindow.axaml`, `WorkbenchWindow.axaml.cs`, `WorkbenchTheme.axaml`,
`WorkbenchStrings.cs`.

- Set the four window properties; add the `WbWindowDecorationsTheme`.
- Add the three caption buttons with roles, glyphs, handlers, state/name updates and tooltips.
- Add the `TitleBar` role and the `User` style.
- Keep the existing topbar content and spacing; append the 138 px cluster and add the compact
  search (36 × 32 icon under 1040 px) so `MinWidth=900` still fits. The document title and the
  1280 px rule wait for S2.

Tests:
- `dotnet build src\Knx.App\Knx.App.csproj -c Release` (0 errors).
- `dotnet run --project src\Knx.App\Knx.App.csproj -c Release --no-build -- --smoke`.
- Existing `tests\Knx.Shell.Tests` (close flow, dialogs, layout).
- New headless `WindowChromeTests`:
  - `WindowDecorations == Full`, `ExtendClientAreaToDecorationsHint`,
    `ExtendClientAreaTitleBarHeightHint == 49`;
  - roles on `Topbar`/caption buttons; `User` on Menu/search/Programming;
  - a hit-matrix helper that mirrors `PresentationSource.GetChromeRoleFromVisual`
    (`InputHitTest` + walk `VisualParent`) at 8 known points;
  - automation names/ids non-empty; maximize name swaps with `WindowState`;
  - the topbar's measured height equals the hint after layout.

Acceptance: window opens without an OS title; min/max/restore/close work; drag/double-click/
resize/snap still work; existing close prompt is reachable from the custom X; screenshots show the
buttons aligned at 1440, 1024 and 900.

### S2 — Topbar integration (risk: medium)

- Add the divider + document title (`MainWindowViewModel.DocumentTitle`, notified with `Title`).
- Add `User` roles to all interactive topbar controls; verify the Menu's first click.
- Add the document title and the 1280 px rule; keep the compact search introduced in S1.
- Restore the exact V4 spacing; do not change the 49 px row or the 30 px existing controls.
- Keep `Ctrl+K`, the palette and the existing click handlers.

Tests/screenshots:

```powershell
dotnet run --project src\Knx.App\Knx.App.csproj -c Release --no-build -- --screenshot=%TEMP%\buschord-frameless\demo-light.png   --screen=demo       --theme=light
dotnet run --project src\Knx.App\Knx.App.csproj -c Release --no-build -- --screenshot=%TEMP%\buschord-frameless\demo-dark.png    --screen=demo       --theme=dark
dotnet run --project src\Knx.App\Knx.App.csproj -c Release --no-build -- --screenshot=%TEMP%\buschord-frameless\small-light.png  --screen=demo-small --theme=light
dotnet run --project src\Knx.App\Knx.App.csproj -c Release --no-build -- --screenshot=%TEMP%\buschord-frameless\tiny-light.png   --screen=demo-tiny  --theme=light
```

- Long project name: set the fixture project name to a 60-character string and confirm ellipsis,
  no overlap with the menus.
- Dirty marker: edit a parameter, confirm the `*` appears in the title and disappears after Save.
- Hit matrix updated for the new title/search points.

Acceptance: the three widths match the wireframe; every strip control clicks on the first click;
clicking empty strip/brand/title drags; no control is clipped; no duplicate project name in the
navigator + topbar confusion (navigator keeps the project name, topbar shows the document title).

### S3 — DPI, maximized, multi-monitor (risk: medium)

- Real 100/150/200 % checks; move the window between monitors with different scaling; verify the
  strip and 46 × 49 buttons stay aligned.
- Maximize/restore: no clipped topbar, no 1 px gap, glyph and name correct, content not shifted.
- Drag from maximized to restore; snap left/right; taskbar thumbnail and Alt+Tab.
- Do not add `WindowDecorationMargin` as padding; it changes between normal/maximized and is
  informational.

Acceptance: checklist signed with screenshots; no manual padding hacks; no blurry 1 px lines at
150 %.

### S4 — Accessibility and high contrast (risk: medium)

- Narrator: window name, button names, maximize → restore name, disabled state, Tab order.
- Keyboard-only: Tab/Shift+Tab reaches all three; Enter/Space activates; focus ring visible on
  light/dark and on the close hover.
- High contrast: read `PlatformSettings.ColorValues.ContrastPreference`; on
  `ColorContrastPreference.High`, add a `high-contrast` class to the topbar and use
  black/white/system accent values for the strip (Win32 `GetSysColor` for `COLOR_WINDOW`,
  `COLOR_WINDOWTEXT`, `COLOR_HIGHLIGHT` is acceptable; it is Windows-first). At minimum, keep the
  glyphs, divider and focus ring visible without relying on the teal palette.
- Re-check automation IDs after the title addition; the window name must not be duplicated by a
  "title bar" control.

Acceptance: a Narrator transcript for the window and the three buttons; a HC screenshot; no
keyboard trap.

### S5 — Regression and rollout (risk: low)

- Full app build, `--smoke`, `--screenshot-selftest`, all UI-relevant test projects
  (`Knx.Shell.Tests`, `Knx.WorkbenchTheme.Tests`, `Knx.WorkbenchUiState.Tests`,
  `Knx.Docking.Tests`, `Knx.ParameterUi.Tests`, `Knx.CatalogUi.Tests`, `Knx.Icon.Tests`,
  `Knx.Monitor.Tests`). Use the same commands as `analysis/avalonia12-package-validation.md`.
- Dialog matrix: Settings, NewProject, Compare, Replace, Report, Confirm, prompt/move/value; all
  still native and closable; modal disables the shell chrome.
- Real checklist from S0 repeated on the integrated build.
- Document the rule in `FEATURE.md` or the UI notes: shell custom chrome, dialogs native, no
  OS fullscreen without a popover, keep `Window.Title`.

Acceptance: all suites green, S0 checklist green, no dialog changes, and a rollback is a single
revert of the chrome commits.

---

## 9. What can and cannot be verified headlessly

| Check | Headless/offscreen | Real Windows session |
|---|---|---|
| Topbar pixels/layout at 1440/1024/900 | yes (`--screenshot`, `RenderTargetBitmap`) | visual review |
| Light/dark tokens, hover rendered by style | partially (no pointer state) | yes |
| Roles on the visual tree, names, enabled state | yes (new tests) | UIA/Narrator |
| Hit matrix (nearest-role walk) | yes (mirrors the production walk) | real `WM_NCHITTEST` |
| Drag, double-click, snap, snap layouts | no | yes |
| System menu, Alt+Space, Alt+F4 | no | yes |
| DWM animations, shadow, rounded corners, resize | no | yes |
| Maximized bounds, multi-monitor, 150/200 % DPI | no | yes |
| Taskbar/Alt+Tab thumbnail | no | yes |
| High contrast, touch | no | yes |

The existing `--screenshot` path renders the client area only; it never shows the native frame,
shadow or caption hit zones. Use it for the strip design, not as proof that the window works.

---

## 10. Risk register

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | `DisableCloseButton` makes Alt+F4 and/or the system-menu Close item inactive | Medium | High | S0; if broken, re-enable `SC_CLOSE` with `GetSystemMenu`/`EnableMenuItem` via `TryGetPlatformHandle` after each state change, or intercept `WM_SYSCOMMAND`; abort if neither is safe |
| R2 | One click on the client maximize button toggles twice (NC redirect + DefWindowProc) | Low | High | S0 single-click test; if broken, move buttons to the managed decorations overlay (Variant B) |
| R3 | An unmarked interactive child resolves to `TitleBar` and drags instead of clicking | Medium | High | `User` style + hit-matrix test + real click matrix (menu, search, programming, buttons) |
| R4 | Open menu does not dismiss when the strip is clicked/dragged | Low | Medium | S0; test with the Project menu open; if needed, close popups on window deactivate or accept |
| R5 | Future Avalonia changes the `Full`/empty-theme behaviour | Medium | Medium | Pin 12.1.4; S0 evidence; Avalonia 12.2's `TitleBarDecorations="None"` (#21969) is the intended migration |
| R6 | Topbar height drifts from `ExtendClientAreaTitleBarHeightHint` | Medium | Low | one shared constant, assertion test, screenshot review |
| R7 | Responsive overflow at 900/1024 with German labels | Medium | Medium | measured breakpoints, icon-only search, three screenshot sizes |
| R8 | 150/200 % misalignment or clipped maximized content | Low | Medium | S3 real DPI/multi-monitor pass |
| R9 | High-contrast regression vs. the native title bar | Medium | Medium | S4 HC class; do not ship without at least a visible-glyph check |
| R10 | Screen-reader focus order/names regress | Low | Medium | names + Narrator transcript; no extra "title bar" control |
| R11 | Taskbar title is lost because someone blanks `Title` to hide the OS text | Low | High | never blank `Title`; assert non-empty in the chrome test |
| R12 | Touch drag on the caption strip behaves differently from mouse | Low | Low | S3 touch check; document if Windows handles it natively |

---

## 11. Recommendation

**Proceed with Variant A, gated on S0.** It is the only option that removes the visible native title
while preserving Windows caption semantics (drag, double-click, snap, snap layouts, system menu,
resize, animations, taskbar and screen-reader name). The implementation is small: four window
properties, one empty decorations theme, three buttons, one role style, a title binding and
responsive rules.

**Do not proceed** if S0 shows that Alt+F4/system-menu Close cannot be restored cheaply, or that a
client `HTMAXBUTTON` button double-toggles. In that case keep the native title bar; `BorderOnly`
(the only other frameless configuration) loses DWM animations and the system menu and is not an
acceptable substitute.

Open decisions for the integrator:

1. Confirm Windows 10 is still supported; if yes, S0 must include it (no snap layouts there, but
   frame/shadow/animation behaviour differs).
2. Decide whether the document title belongs in the topbar (recommended, max 180 px) or only in
   the navigator. If it stays only in the navigator, the 1280 breakpoint and the title work in S2
   disappear.
3. Decide whether high contrast is a release blocker for this change. The native title bar adapted
   automatically; the custom strip does not. I recommend treating it as a blocker (S4).
4. Keep the dialog decision (native everywhere) unless there is a separate request for dialog
   chrome.
