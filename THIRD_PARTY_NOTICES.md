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
