# NEXORA architecture

## Layers

```
┌───────────────────────────────────────────────────────────────┐
│ Nexora.App  (WinUI 3, MVVM)                                   │
│  Views (XAML, x:Bind) → ViewModels → App services             │
│  NavigationService · ThemeService · DialogService ·           │
│  OptimizationCoordinator · LiveScoreService · Tray · Notify   │
├───────────────────────────────────────────────────────────────┤
│ Nexora.Core  (net10.0, no Windows dependency, unit tested)    │
│  OptimizationEngine · Catalog · Backup/History stores         │
│  MetricsMonitor · FrameStatistics · PerformanceSession        │
│  NetworkTestRunner · NetworkMonitor · LatencyStatistics       │
│  SystemScanner · PerformanceScoreCalculator · Parsers         │
│  Abstractions: IRegistryAccessor, IPowerManager,              │
│  ISystemParameters, IMetricsProvider, IFrameRateSource, …     │
├───────────────────────────────────────────────────────────────┤
│ Nexora.Platform.Windows  (net10.0-windows, P/Invoke)          │
│  Registry · powrprof · SystemParametersInfo · PDH · DXGI ·    │
│  D3DKMT · ETW (DXGI/D3D9 present events) · iphlpapi · wlanapi │
│  Process service · Game detection/launcher · Tray icon        │
└───────────────────────────────────────────────────────────────┘
```

Dependencies only point downward. Core defines interfaces, the platform layer implements them, and the app
composes everything in `App.ConfigureServices` (Microsoft.Extensions.DependencyInjection, validated on build).

## Key flows

### Applying an optimization

```
OptimizationItemViewModel.Apply
  → OptimizationCoordinator.ApplyAsync     (explain + confirm, elevation check)
    → OptimizationEngine.ApplyAsync        (serialized by a semaphore)
        GetState → CreateBackup → BackupStore.Save (disk)  ← nothing changed yet
        Apply → Save backup again → GetState (verify)
        success → HistoryStore.Add(Applied)
        failure → Restore(backup) → History(Failed)  [or batch rollback]
```

Undo loads the backup by id and calls `RestoreAsync`. Only the newest active change of an optimization can be
undone, so restores never apply out of order.

### Live metrics

`MetricsMonitor` runs one background loop. It samples `WindowsMetricsProvider` at the configured interval
while the window is visible, every 5 s when hidden with background monitoring on, and not at all otherwise.
Performance tests and scans hold an `ActiveRequest`, which keeps sampling alive. Subscribers (view models)
marshal to the UI thread through `UiDispatcher`. Pages subscribe in `OnNavigatedTo` and unsubscribe in
`OnNavigatedFrom`, so hidden pages cost nothing.

### FPS

`EtwPresentMonitor` starts a private real-time ETW session (`NEXORA-FrameCapture`), enables the DXGI and
Direct3D 9 providers, and counts `Present_Start` events per process. The time between consecutive presents is
the frame time. The target is the explicitly chosen process, else the foreground window's process, else the
busiest renderer. The session is stopped explicitly on exit. A session left behind by a crash is detected and
replaced on the next start.

## Design rules

* **No fake data.** Every value is either measured or `null`, which the UI shows as *Unavailable on this system*.
* **Least privilege.** The app runs `asInvoker`. Admin-only operations are refused with an explanation and
  offered via *Restart as administrator*.
* **No shell execution.** External processes are limited to validated `.exe` paths chosen by the user,
  allow-listed store URIs (`steam://rungameid/<n>`, Epic launch URIs) and `explorer.exe <folder>`.
* **Crash-safe persistence.** JSON is written to a temp file, flushed, then atomically renamed.
  Corrupt files are preserved and replaced with defaults.
* **Lightweight.** Single sampling loop, PDH queries reused, D3DKMT telemetry every other sample, charts
  redraw only on data change, and animations use the compositor (implicit transitions / `CompositionTarget`)
  only while running.
