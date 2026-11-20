# Third-party notices

## IEEE OUI registry (via oui-data)

`src/CameraFinder.Core/Resources/oui.tsv.gz` is derived from the IEEE Registration Authority's public
MA-L, MA-M and MA-S assignments, as packaged by the `oui-data` project (version 2.1.52).
Only the first line of each organisation name is kept.

oui-data — BSD 2-Clause License

Copyright (c) silverwind
All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided
that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the
   following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the
   following disclaimer in the documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED
WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE
OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH
DAMAGE.

## UniversalScanner (reference only, no code included)

The manufacturer discovery protocols in `src/CameraFinder.Core/Discovery/VendorProbes.cs` (Hikvision SADP,
Dahua, Hanwha, Bosch, i-PRO/Panasonic, Vivotek, Sony, Ubiquiti, Axis/Arecont mDNS) were written for Camera
Finder. Their packet layouts (ports, magic numbers, field offsets) were learned from UniversalScanner by
Julien Blitte, https://github.com/julienblitte/UniversalScanner, which is licensed under the GNU LGPL v3.
No UniversalScanner source code is included in this project. Thanks to its author for documenting these
protocols.

## Npcap (not included)

Passive listening uses Npcap if the user has installed it separately (https://npcap.com). Npcap is not
redistributed with this software.

## FFmpeg (ffprobe.exe, included)

The installer includes `tools\ffprobe.exe` from FFmpeg 9.0.2 (the "essentials" static Windows build by gyan.dev),
used by the health check to watch a camera's video stream. It is a separate program that Camera Finder runs; it is
not linked into Camera Finder.

FFmpeg is licensed under the GNU General Public License version 3 (this build includes GPL components). The full
licence text is installed as `tools\licenses\FFmpeg-LICENSE.txt`. The corresponding source code is available at
https://github.com/FFmpeg/FFmpeg/commit/946fcce07b (build configuration: https://www.gyan.dev/ffmpeg/builds/);
`tools\licenses\SOURCES.txt` lists the same addresses.

## iperf3 and Cygwin (iperf3.exe, cygwin1.dll, included)

The installer includes `tools\iperf3.exe` (iperf3 3.22, Windows build by ar51an/iperf3-win-builds) and the
`cygwin1.dll` it needs, used by the speed test server. They are separate programs that Camera Finder runs.

- iperf3 — BSD 3-Clause style licence, © The Regents of the University of California, through Lawrence Berkeley
  National Laboratory. The full licence (including the notices for the code iperf3 contains) is installed as
  `tools\licenses\iperf3-LICENSE.txt`. Source: https://github.com/esnet/iperf/releases/tag/3.22
- Cygwin (`cygwin1.dll`) — GNU Lesser General Public License version 3 (https://www.gnu.org/licenses/lgpl-3.0.html,
  which builds on the GPL v3 installed as `tools\licenses\FFmpeg-LICENSE.txt`). Source: https://cygwin.com/

Both are downloaded at build time from pinned releases and checked against a SHA-256 before they are packaged (see
`tools/fetch-tools.ps1`).
