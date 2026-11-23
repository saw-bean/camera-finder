# Camera Finder

**[⬇ Download for Windows (latest installer)](https://github.com/saw-bean/camera-finder/releases/latest/download/CameraFinder-Setup.exe)** · [All releases](https://github.com/saw-bean/camera-finder/releases)

A Windows tool for security installers and service technicians. Plug into the site network, press **Scan**, and it lists every camera, door controller and other device on the cable — **any brand** — with its IP, manufacturer and web page. Click **Open web page** to go straight to the device's login page.

It works at Layer 2 (Ethernet/ARP), which every networked device must use, so it does not depend on each brand's own discovery protocol. It also finds devices whose IP is in a range you don't know: it hears them on the wire, temporarily joins that range, and sweeps it.

## How it works

| Step | What the app does |
|---|---|
| 1. Listen | Captures ARP, DHCP and broadcast/multicast traffic (needs Npcap) to learn which IP ranges are on the cable. |
| 2. Probe | Sends ONVIF (WS-Discovery) and UPnP (SSDP) searches, plus the manufacturers' own "finder tool" protocols (see below), so quiet cameras answer. |
| 3. Join ranges | For each new range, adds a temporary IP to the laptop (e.g. `10.0.5.251`). Windows checks the address is free first; if another device uses it, another is tried. |
| 4. ARP sweep | ARPs every address in each range. Every device must answer ARP, even if it ignores ping. |
| 5. Details | Looks up the manufacturer from the MAC (offline IEEE database), checks ports 80/443/554/8000/8080, reads the web page title and ONVIF/UPnP model. |
| 6. Clean up | Removes the temporary IPs when you close the app — and on the next start if the app crashed. |

The temporary IPs are added with `skipassource`, so the laptop's normal traffic keeps using its own address. On DHCP adapters, Windows' *DHCP/static coexistence* is turned on during the scan and turned off again afterwards.

## Install

1. [Download the latest installer](https://github.com/saw-bean/camera-finder/releases/latest/download/CameraFinder-Setup.exe) (or pick a version from the [Releases](../../releases) page).
2. Run it. The .NET runtime is included; nothing else is required.
3. **Npcap (recommended):** keep *Download and install Npcap* ticked. Setup downloads the official installer from npcap.com (checked against a pinned SHA-256) and starts it; click through Npcap's own wizard. The option is hidden if Npcap is already installed (e.g. with Wireshark). Without Npcap, step 1 (passive listening) is off, so the app only finds devices in ranges your laptop is already in, plus devices that answer ONVIF/UPnP. Offline? Setup offers the [download page](https://npcap.com/#download) instead.

> Npcap's free licence allows up to 5 computers and does not allow it to be bundled inside another installer, which is why setup downloads it from npcap.com rather than including it. Nothing enforces the 5-computer limit; staying within it is up to you (more needs an [Npcap OEM](https://npcap.com/oem/) licence).

Requirements: Windows 10 version 2004 or later / Windows 11, administrator rights (to add temporary IPs).

### Updates

Updates install themselves. On start, the app asks GitHub (`api.github.com`) whether a newer release exists. If one does, it downloads the installer in the background, checks it against the SHA-256 GitHub publishes for it, and installs it silently when you close Camera Finder (after the temporary IPs are removed), so the next start is the new version. Nothing interrupts you on site; the status bar says an update is waiting, and clicking it installs right away and restarts the app. With no internet (common on site networks) the check is silently skipped and tried again next start.

To publish an update (maintainers): push a version tag, e.g. `git tag v0.2.0 && git push origin v0.2.0`. CI builds the installer and attaches it to a new release.

## Use

1. Plug the laptop into the camera/door network (the NVR's switch is a good spot).
2. Start **Camera Finder**, pick the wired adapter, press **Scan**.
3. Wait for the listen time (30 s default — longer finds more quiet devices).
   Looking for one device? Type its IP or MAC (or the last digits of the MAC on its label, e.g. `3456`) in **Stop when found** before pressing Scan: the scan stops as soon as it appears and selects it. **Stop** at any time also keeps everything found so far.
4. Double-click a device, or select it and press **Open web page** (works during a scan too - the scan stops first).
5. **Export CSV…** saves the list for the job file.
6. Close the app (or press **Remove temporary IPs**) when you're done.

**Open web page for any device.** If a device is in a range the laptop isn't in (e.g. a new Hikvision on `192.0.0.64`, or a range beyond the 6 joined automatically), *Open web page* first adds a temporary IP in that range (e.g. `192.0.0.251`), checks the device's ports, then opens its page. In safe mode it asks before adding the IP. Like all temporary IPs, it is removed when you close the app or press *Remove temporary IPs*.

**While the page is open, the laptop switches over to the device's network.** A temporary IP alone is not enough for the browser: it is added with `skipassource`, so Windows never sends from it and the device would never see a reply address in its own range. When you open the page, Camera Finder makes that IP a normal (main) address and adds a route to the device through the scan adapter (active only, never saved), so the browser and any plugin reach the device just as if you had set the laptop's IP by hand. This also wins over Wi-Fi or a VPN that uses the same range. A small *Web page open* window shows what changed. Close it (or press *Done - restore network*, open another device's page, press *Remove temporary IPs*, or close the app) and everything goes back to normal. If the app crashes, the change is undone on the next start. Internet on that adapter may not work while the page is open. When the laptop already has a normal address in the device's range, nothing is changed.

**Ping** (continuous, like `ping -t`) opens a window for the selected device: one line per second, running totals (sent, lost %, min/avg/max) and a clear **DOWN** / **UP** line when it stops or starts answering - handy while a camera reboots or a cable is moved. Open several at once. For a device in another range it adds a temporary IP first (asks in safe mode) and pings from it.

**Demo (test site)** scans a simulated site instead of the real network: 12 devices of different brands spread over five IP ranges, including quiet cameras, a door controller, a camera on its factory-default IP and one with a self-assigned IP. Nothing is sent and the laptop's settings are not changed, so it is safe for testing and training anywhere. *Open web page* in demo shows the temporary-IP step without opening a browser.

**Safe mode** only listens and sends discovery probes. It never changes the laptop's network settings and never sweeps. Use it on sensitive client networks.

### Manufacturer discovery protocols

Many cameras answer their maker's own finder tool even when ONVIF is off, and often even when their IP is in a different range than the laptop's (they reply by broadcast/multicast). Camera Finder speaks these protocols, focusing on brands common in Canada:

| Protocol | Brands |
|---|---|
| Hikvision SADP | Hikvision, HiLook and Hikvision OEMs — also flags cameras that are **not activated** yet |
| Dahua (port 5050 and DHIP 37810) | Dahua, Lorex, Amcrest and Dahua OEMs |
| Hanwha | Hanwha Vision / Samsung Wisenet |
| Bosch | Bosch IP cameras and encoders |
| i-PRO / Panasonic | i-PRO, Panasonic |
| Vivotek | Vivotek |
| Sony | Sony IP cameras |
| Ubiquiti | UniFi Protect cameras (and other UniFi gear) |
| mDNS / Bonjour | Axis, Arecont Vision |

Avigilon, Uniview, Hanwha's newer models and most others are found through ONVIF, SSDP and the ARP sweep. These are read-only "who is there?" requests, so they also run in safe mode. Packet layouts were worked out with help from [UniversalScanner](https://github.com/julienblitte/UniversalScanner) by Julien Blitte; Camera Finder's implementation is its own (see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)).

## Camera health tools

**Select a device and everything runs by itself.** The panel on the right fills in automatically: who the device is, its health diagnosis, the **live bandwidth** its video stream uses, the ffmpeg stream check, and buttons for its web page, Ping, Trace and Copy IP. Selecting another device stops the checks on the previous one. During a scan the checks wait until the scan finishes. Outside safe mode a temporary IP is added automatically for devices in other ranges (in safe mode the panel offers a button instead).

**Switch port bandwidth (SNMP)** works for any device, not just cameras. After a scan the app asks the devices it found, once, which of them are managed switches (SNMP v2c, read-only, community `public` or one you enter in the panel - it is saved). When you select a device it looks up which switch port its MAC address is learned on (BRIDGE-MIB / Q-BRIDGE-MIB) and shows, every 2 seconds, what the device sends and receives on the wire, plus the port's name, link speed, duplex and CRC errors. Half duplex, 10 Mbit/s links, CRC errors and a link that is down become findings in the diagnosis. If the device is behind an unmanaged switch, the panel says its port is shared and the traffic includes the other devices. SNMPv3-only and unmanaged switches cannot be read.

**Path (trace)** is filled in automatically: on a flat camera network it is one hop (the device itself); across routers or a VPN every hop is listed with its delay and loss. *Trace* opens the live mtr-style window, which can also trace to any other address or name (the NVR, the gateway, 8.8.8.8).

**Video stream bandwidth (RTSP)** plays the camera's stream with Camera Finder's own RTSP client (RTP over TCP) and shows, every second, the Mbit/s on the wire, frames per second, key-frame spacing, peak and average, and any packets the camera skipped, with a two-minute graph and the bitrate limit configured in the camera as a dashed line. Pick the main or sub stream when the camera has several. It measures one viewer's copy of the stream; the NVR pulls its own copy, so a camera's total load is this times the number of viewers.

**Live view delay** is for "the live view lags, whatever stream settings I change". From the live stream the panel measures, for every camera: the time to the first picture (what makes a live view slow to open), how unevenly frames arrive (a player must buffer that much, and buffering is delay), and whether the delay builds up over time (the path cannot keep up). **Measure camera delay (flash test)** times the camera itself: the laptop screen flips between a checkerboard and its inverse; point the camera at the screen and the app times how long each flip takes to show up in the camera's stream (the flip makes the camera send one much bigger frame, so nothing has to be decoded). That is the camera + network delay with no NVR in between. If it is fast (typically 0.1-0.5 s) but the live view you watch lags seconds, the diagnosis explains that the delay is added after the camera - the NVR, the viewing software's buffer setting, a cloud/P2P app or the viewing PC - and what to change; if the camera itself is slow, it lists the camera settings that hold frames back.

**Health check** works out what is wrong with a camera and says so in plain words. It runs in four steps:

1. **Collect** — 20 pings, an mtr-style path trace, ONVIF (model and firmware, the camera's clock, its **system log**, video profiles and stream addresses) and the live stream (ffprobe for codec, resolution and frame timing; the live bandwidth monitor for everything else).
2. **Normalize** — every measurement and every line of the camera's own log is rewritten as one plain sentence. Brands word things differently ("Illegal login", "authentication failure", "Login failed"); they all become *"A login was refused (wrong user name or password) from 192.168.1.10"*.
3. **Detect** — known failure patterns are matched across the events, for example:
   | Pattern | What it usually means |
   |---|---|
   | Several start-ups in a day, no crash in the log | The camera is losing power: PoE budget, injector, long or CCA cable |
   | Watchdog / crash restarts | Firmware fault — update, turn off unused features |
   | Many refused logins from one address, account locked | The NVR/VMS or a phone app still has an old password |
   | Refused logins from internet addresses | The camera is exposed through a port forward |
   | Camera clock minutes or hours off | NTP off or unreachable — recordings hard to find, ONVIF logins fail |
   | Ping loss on the last hop only | The camera's cable, connector or switch port |
   | Loss starting at an earlier hop | That router/switch link, not the camera |
   | Fewer frames than configured, pauses in the stream | Packet loss or an overloaded link/camera |
   | Key frames more than 4 s apart | Slow live view start, coarse NVR seeking |
   | IP conflict, SD card errors, video loss, overheating, tampering | As named, from the camera's log |
4. **Explain** — a short diagnosis: the most likely problem first, the evidence, what to try first, then what looked fine and the camera's recent events. *Events* shows the full plain-language timeline and *Collected data* the raw values and log.

The camera's user name and password are needed for the log and video. The panel tries the login saved for that camera, else the last login that worked (a site usually shares one). One login serves every check: ONVIF (WS-Security password digest, timed with the camera's own clock, or HTTP digest) for the log, settings and stream addresses, and RTSP (Basic/Digest) for the live bandwidth, delay and ffmpeg checks. A login that works on either is saved automatically, encrypted for your Windows user (DPAPI, `%LOCALAPPDATA%\CameraFinder\logins.dat`), and tried first on the next camera. ONVIF and video are tracked separately, because many cameras (Hikvision, Dahua, Axis…) keep ONVIF users apart from their web/RTSP users: when ONVIF refuses a login, the video still tries it, and if the video plays the diagnosis says *"The video accepts the login but ONVIF does not"* with where to add the ONVIF user. A login a service **refuses is never retried on it automatically** — the panel asks for the right one — so clicking through cameras cannot lock their accounts. For a camera reached through a temporary IP, ffprobe cannot connect (it always uses the laptop's main address), so the video check comes from the live bandwidth monitor instead, which can.

**Trace** is mtr for Windows: every router on the way to the device, probed every second, with loss, last/average/best/worst delay and jitter per hop, and a sentence saying where loss starts. On a flat camera network it shows one hop — the device itself.

**Speed test (iperf3)…** runs an iperf3 server on the laptop. At the other end of the cable run (the camera's location, another switch, the NVR rack), run an iperf3 client on a second laptop or phone: `iperf3 -c <laptop IP>` (add `-R` for the other direction, `-u -b 50M` for UDP loss and jitter). Each result is explained: whether it matches a gigabit, 100 Mbit/s or 10 Mbit/s link (100 Mbit/s on a gigabit run is a classic sign of a damaged pair), the UDP loss, and how many cameras the link can carry. Setup adds a Windows Firewall rule for the bundled iperf3 on private and domain networks.

The Demo site has a simulated fault on several cameras (restart loop on 192.168.1.108, NVR with an old password and a slow clock on 192.168.1.64, a lossy cable on 10.0.5.40, a failing SD card on 172.16.8.30, separate ONVIF users on 192.168.1.120 — ONVIF login `onvif` / `onvif123`), so all of this can be tried without real cameras.

## Limits

- **One VLAN at a time.** Layer 2 stops at the VLAN boundary — plug into each network separately.
- **Devices with no IP yet** (waiting for DHCP) are not listed; the app never runs a DHCP server, because a rogue DHCP server can break a client's network.
- **Devices that never transmit**, in a range nothing else uses, can only be found if they answer ONVIF/UPnP.
- **Manufacturer** is the company the MAC address is registered to. It can be a parent company or chip maker (e.g. Avigilon cameras show as *Motorola Solutions*; some controllers show the network module maker).
- **No web page** (some door controllers): use the manufacturer's software (e.g. EntraPass/KT-Finder for Kantech).
- The app never guesses passwords or changes device settings. It logs in only with a login you typed or saved, and only to read (ONVIF queries, RTSP video, SNMP reads).

## Use responsibly

Only scan networks you are authorised to work on. On a client's live network, get approval from your supervisor or the client's IT first: ARP sweeps and port checks can trigger network security alerts.

## Build from source

```
dotnet run --project tests/CameraFinder.Core.Tests -- --unit-only     # tests
dotnet publish src/CameraFinder.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
iscc installer\CameraFinder.iss                                          # installer (Inno Setup 6)
```

GitHub Actions builds the installer on every push (`.github/workflows/build.yml`). Push a tag like `v0.1.0` to publish a release.

### Project layout

```
src/CameraFinder.Core   discovery engine (no external dependencies)
  Capture/              passive listening via Npcap/libpcap, Ethernet/ARP frame parser
  Discovery/            ONVIF + SSDP probes, ARP sweep, port/web checks, ScanEngine
  Net/                  adapters, subnet maths, temporary address manager, OUI database
  Export/               CSV export
src/CameraFinder.App    Windows Forms app
tests/                  test runner (unit tests + live tests with a fake camera)
installer/              Inno Setup script
```

## Licence

MIT — see [LICENSE](LICENSE). Third-party data: see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
