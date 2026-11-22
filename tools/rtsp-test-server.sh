#!/usr/bin/env bash
# Starts a real RTSP server (mediamtx) with a login, fed by ffmpeg with a test pattern, so the live bandwidth
# monitor and the ffmpeg check can be tested without a camera. Used by CI; also handy on a dev machine.
#
#   tools/rtsp-test-server.sh <workdir> [digest|basic]
#   -> rtsp://127.0.0.1:8554/cam   login admin / secret   H.264 1280x720, 25 fps, ~2 Mbit/s, key frame every 2 s
#
# Needs ffmpeg on PATH. mediamtx is downloaded (pinned version, SHA-256 checked) into <workdir>.
# Stop with: kill $(cat <workdir>/pids)
set -euo pipefail
work="${1:?workdir}"; auth="${2:-digest}"
version=v1.21.3
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)  asset=linux_amd64;  sha=0a2ea7eba96023631efa1bea7804823a9145b043b6646a8a05798d4ac24ed635 ;;
  Darwin-arm64)  asset=darwin_arm64; sha=bc1a0762fd18ffb413bcd81dcb9a072ac159fbd1f27e79ac6f14598aaac5ed3f ;;
  *) echo "unsupported platform $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac
mkdir -p "$work"
if [ ! -x "$work/mediamtx" ]; then
  curl -sSL -o "$work/mediamtx.tgz" "https://github.com/bluenviron/mediamtx/releases/download/$version/mediamtx_${version}_${asset}.tar.gz"
  echo "$sha  $work/mediamtx.tgz" | shasum -a 256 -c - >/dev/null
  tar -xzf "$work/mediamtx.tgz" -C "$work" mediamtx
fi

# Anyone on this machine may publish (ffmpeg); reading needs admin / secret, like a camera.
cat > "$work/mediamtx.yml" <<EOF
logLevel: warn
authMethod: internal
authInternalUsers:
  - user: any
    pass:
    ips: ["127.0.0.1"]
    permissions:
      - action: publish
  - user: admin
    pass: secret
    ips: []
    permissions:
      - action: read
rtspTransports: [tcp]
rtspAuthMethods: [$auth]
rtmp: false
hls: false
webrtc: false
srt: false
paths:
  all_others:
EOF

"$work/mediamtx" "$work/mediamtx.yml" > "$work/mediamtx.log" 2>&1 &
echo $! > "$work/pids"
sleep 1
ffmpeg -hide_banner -loglevel error -re -f lavfi -i testsrc2=size=1280x720:rate=25 \
  -c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p -g 50 -keyint_min 50 -sc_threshold 0 \
  -b:v 2M -maxrate 2M -bufsize 2M -f rtsp -rtsp_transport tcp rtsp://127.0.0.1:8554/cam > "$work/ffmpeg.log" 2>&1 &
echo $! >> "$work/pids"
sleep 3
echo "rtsp://127.0.0.1:8554/cam ($auth login admin / secret)"
