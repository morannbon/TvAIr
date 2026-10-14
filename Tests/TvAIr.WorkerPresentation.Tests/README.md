# Worker presentation regression tests

This suite tests the built TvAIrEpgRec assembly and its real Windows forms. No tuner,
BonDriver, TVTest, card reader, or EPG database is opened. Tests create a private
Windows desktop and launch child processes there. They never switch the user's
input desktop or move focus on the user's working desktop.

Build for the architecture of the worker DLL, then run the generated EXE:

```powershell
dotnet build Tests/TvAIr.WorkerPresentation.Tests -c Release -p:Platform=x64 -warnaserror
& ./Tests/TvAIr.WorkerPresentation.Tests/bin/x64/Release/net8.0-windows/TvAIr.WorkerPresentation.Tests.exe `
  ./TvAIrEpgRec/bin/x64/Release/net8.0-windows/TvAIrEpgRec.dll ./focus-results-x64
```

Repeat with x86 paths and Platform=x86. Creating the private desktop requires a
normal interactive Windows session with permission to create a desktop object.
The suite fails rather than falling back to testing on the user's working desktop.

Coverage:

- WinForms' minimized ShowParams chooses SW_SHOWMINIMIZED before ShowWithoutActivation.
- All launch kinds and both visibility settings produce one explicit policy argument.
- Initial native display remains minimized and preserves the active input window.
- Managed visibility, child controls, logo and icon remain functional.
- Deliberate user restoration works; later content updates do not minimize or activate it.
- Hidden workers remain without a UI thread/window after progress and recording updates.
- EPG and recording use the same presentation owner and create one taskbar window.
- Eight actual worker process launches alternate visible and hidden presentation.

The baseline records whether activation actually reproduced on the isolated
desktop, separately from verifying the activating show-command selection.
Passing this suite does not establish that the reported real CATV/game environment
has been reproduced or that a third-party BonDriver never creates its own UI.

The worker presentation tests include background-window style and OS fallback checks, 16 segment
presentation updates, serial hidden/taskbar transitions, parallel worker processes,
and developer focus telemetry checks. Process-transition fixtures run mode=runtime
without a tuner. The epg/record/chain cases exercise real presentation callbacks,
not full EPG acquisition or native recording. There is no Explorer taskbar on the
private desktop. Manual native restore remains covered; this is not an actual
Explorer taskbar mouse-click test.
The same progress write puts queued worker_focus records before the canonical
lifecycle row. The terminal completed stage must remain last.