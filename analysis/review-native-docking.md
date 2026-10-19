# V1: native pointer/key validation

2026-10-19. Validation only; no application/package changes.

## Build and isolation

- Branch `agents/etsfix/v1-pointer`, starting at verified `8facbcab01ca634a81f5417970ca3e629ad9b490`.
- Debug net9.0 build of this worktree, loaded by an external harness using the actual `Knx.App.App`/`MainWindow` and platform backend.
- Inspected assembly: `C:\Users\julia\AppData\Local\Temp\opencode\etsfix-v1\bin\Debug\net9.0\BusChord.dll`.
- SHA-256: `72B335BD205E446CC481AA1CDBDEB0F94AA28D0C61C4C8B986ADDEC08FA384F1`.
- Display 1920×1200; main client 1440×900 at screen position (100,70); Avalonia render scale 1 (96-DPI equivalent). Floating client 866×795, initially partly beyond the right/bottom screen edge.
- Harness creates its own one-device synthetic project and per-scenario `BUSCHORD_SETTINGS`; shared catalogue store replaced with an empty external directory before app setup. No private projects, bus actions, or unrelated processes touched.
- Native `SetCursorPos` + `SendInput` mouse-down/move/up and keyboard events. Foreground PID checked against the launched PID before input. No docking-factory calls and no raised Avalonia input events. Harness only prepares fixture/undo sentinel and observes rendered controls/state.

## Observed results

| Scenario | Actual gesture, screen pixels | Observation |
|---|---|---|
| Topology float, fresh PID 34972 | Header (148,189) → (1650,550) | **Crash**: `InvalidOperationException`, `Border (Name = TopologyPanel) already has a visual parent ContentPresenter`. Real pointer reachability confirmed. |
| Properties float, fresh PID 28728 | Header (1279,189) → (1650,550) | **Crash**: same exception for `PropertiesPanel`. Real pointer reachability confirmed. |
| Document float, fresh PID 27784 | Communication objects tab (837,190) → (1650,550) | Real HostWindow created, document content visible. |
| Document drag back, PID 27784 | Floating document tab (1650,581) → main document center (800,480) | Content returned to main; **no crash observed**. Two empty HostWindows remained in lifetime inventory. |
| Physical Ctrl+Z, fresh PID 5328 | Float document via tab (829,190) → (1650,550); click floating tab (1650,581), SendInput Ctrl-down/Z-down/Z-up/Ctrl-up | **Pass for dispatch**: float focus `DocumentTabStripItem`; device name `UNDO SENTINEL` → `Synthetic device`, CanUndo true → false, CanRedo false → true. Sentinel created through public `ViewModel.Edit`, not keyboard input. |
| Repeat document drag back, PID 5328 | Floating document tab (1650,581) → (800,480) | Content returned to main without crash; **two empty HostWindows still IsVisible=true**, one original and one near drop point. Not a clean lifecycle pass. |

The first PID-5328 drag ended inside main at (1400,380); it did not float. This is logged as an unsuccessful float attempt, not counted as a float pass.

**Conclusion:** both known tool-float crashes are reachable by actual native pointer input. The factory-level document-redock exception was **not reproduced** with these two native drag-back gestures; instead an empty-floating-window lifecycle defect was observed. Ctrl+Z from a focused floating tab dispatches successfully in the tested build.

## Retained evidence

All external artifacts are under `C:\Users\julia\AppData\Local\Temp\opencode\etsfix-v1\`:

- Reproduction: `Harness.csproj`, `Program.cs`, `native.ps1`, `gestures.log`; `*-synthetic.knxproj`, `*-settings.json`.
- Complete crash stacks: `topology-stderr.log`, `properties-stderr.log`; corresponding `topology.log`/`properties.log` contain unhandled-exception records.
- Before crash images: `topology-before-MainWindow.png`, `properties-before-MainWindow.png`. No after-crash app screenshot exists because the process terminated during layout.
- Document run: `document.log`, `document-before-MainWindow.png`, `document-after-MainWindow.png` (content redocked), `document-after-HostWindow.png` (empty host).
- Key/second document run: `key.log`, `key-before-MainWindow-084549979.png`.
- Before key: `key-after-HostWindow-084614339.png`; after key: `key-after-HostWindow-084624474.png`; state transition at `key.log` lines 224–303.
- After second redock: `key-after-MainWindow-084644589.png`, `key-after-HostWindow-084644853.png`, `key-after-HostWindow-084644934.png`; visible empty hosts recorded at `key.log` lines 389–390.

Images are in-process RenderTargetBitmap snapshots of this app only, not desktop captures; native coordinates and real window creation are recorded independently. Intermediate first-run float image was overwritten by the original single-name screenshot routine; second-run timestamped float images are retained.

## Limits

No claim of universal redock safety: only the Communication objects tab and center-drop gesture were tested. No edge/split targets, tool close/redock (blocked by float crash), float-focused editable text Ctrl+Z, multi-monitor, non-100% scaling, or installed release executable validation. The empty-window cause is not investigated here. Own document PID 27784 was terminated after evidence collection to rebuild the external harness; no pre-existing application was terminated.
