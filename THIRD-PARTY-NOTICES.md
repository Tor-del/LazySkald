# Third-party notices

`src/ScreenReader.Edge` is a C# adaptation of the Edge speech protocol and v2
token algorithm from [rany2/edge-tts](https://github.com/rany2/edge-tts), retrieved
2026-11-02. Copyright rany and edge-tts contributors. It is licensed under
LGPL-3.0-or-later. The implementation has been translated to C#, uses .NET
WebSockets, and omits subtitle/metadata support. The v2 digest is ported from
`src/edge_tts/drm.py`; the original attribution and license are preserved.

See `licenses/edge-tts-LICENSE.txt` and `licenses/GPL-3.0.txt`. The Edge library
is a separate replaceable DLL. Its corresponding source and project are included
in the portable build under `source/ScreenReader.Edge`, together with its
`source/ScreenReader.Speech` contract dependency. To rebuild, install the
.NET 10 SDK and run `dotnet build source/ScreenReader.Edge -c Release`; replace
`ScreenReader.Edge.dll` with the resulting DLL. Modification and reverse
engineering of this library for debugging modifications are permitted under LGPL.

Other dependencies:

- Markdig 1.5.1 — Alexandre Mutel, BSD-2-Clause.
  https://github.com/xoofx/markdig. See `licenses/Markdig-BSD-2-Clause.txt`.
- NAudio 2.2.1 — Mark Heath and contributors, MIT.
  https://github.com/naudio/NAudio
- Vortice.Direct3D11 / Vortice.DXGI 3.8.3 and Vortice.Mathematics — Amer Koleci
  and contributors, MIT. https://github.com/amerkoleci/Vortice.Windows
- SharpGen.Runtime — SharpGenTools contributors, MIT.
  https://github.com/SharpGenTools/SharpGenTools
- Microsoft Windows SDK .NET / C#/WinRT and .NET runtime — Microsoft and
  contributors; applicable notices are included with the runtime/packages.

The app does not include or run the GPL-licensed `travisvn/openai-edge-tts` server.
It is not affiliated with Microsoft and does not use an Azure subscription key.
