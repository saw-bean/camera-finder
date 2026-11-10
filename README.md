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

On start, the app asks GitHub (`api.github.com`) whether a newer release exists. If one does, the status bar shows **Update available – click to install**: the app downloads the new installer, closes (removing temporary IPs) and starts setup, which upgrades in place. With no internet (common on site networks) the check is silently skipped.

To publish an update (maintainers): push a version tag, e.g. `git tag v0.2.0 && git push origin v0.2.0`. CI builds the installer and attaches it to a new release.

## Use

1. Plug the laptop into the camera/door network (the NVR's switch is a good spot).
2. Start **Camera Finder**, pick the wired adapter, press **Scan**.
3. Wait for the listen time (30 s default — longer finds more quiet devices).
4. Double-click a device, or select it and press **Open web page**.
5. **Export CSV…** saves the list for the job file.
6. Close the app (or press **Remove temporary IPs**) when you're done.

**Open web page for any device.** If a device is in a range the laptop isn't in (e.g. a new Hikvision on `192.0.0.64`, or a range beyond the 6 joined automatically), *Open web page* first adds a temporary IP in that range (e.g. `192.0.0.251`), checks the device's ports, then opens its page. In safe mode it asks before adding the IP. Like all temporary IPs, it is removed when you close the app or press *Remove temporary IPs*.

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

## Limits

- **One VLAN at a time.** Layer 2 stops at the VLAN boundary — plug into each network separately.
- **Devices with no IP yet** (waiting for DHCP) are not listed; the app never runs a DHCP server, because a rogue DHCP server can break a client's network.
- **Devices that never transmit**, in a range nothing else uses, can only be found if they answer ONVIF/UPnP.
- **Manufacturer** is the company the MAC address is registered to. It can be a parent company or chip maker (e.g. Avigilon cameras show as *Motorola Solutions*; some controllers show the network module maker).
- **No web page** (some door controllers): use the manufacturer's software (e.g. EntraPass/KT-Finder for Kantech).
- The app never logs in, guesses passwords or changes device settings.

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
