# Глечики — liquidsoap pipeline. Runs natively on Windows (tools\liquidsoap, started by start.ps1), no Docker/Icecast.
# Priority: user queue -> auto-DJ queue -> spare (cached tracks listed by the server) -> silence.
# Controlled by the Hlechyky server over telnet (127.0.0.1:1234):
#   userq.push <uri> | autoq.push <uri> | userq.queue | userq.remove <rid> | userq.skip | autoq.skip | spare.skip
#   radio.remaining | radio.metadata (newest block first) | listeners
# The stream itself is served by liquidsoap's harbor on 127.0.0.1:$HARBOR_PORT/radio.mp3; Caddy proxies it to
# https://…/radio.mp3 and to http://…:8000/radio.mp3 (ETS2), so liquidsoap never faces the internet.

settings.log.level := 3
settings.log.stdout := true
# start.ps1 catches stdout into logs\liquidsoap.log. The built-in log file would also be a trap: liquidsoap's
# path substitution breaks on Windows paths that contain "\<digit>".
settings.log.file := false
settings.server.telnet := true
settings.server.telnet.bind_addr := "127.0.0.1"
settings.server.telnet.port := int_of_string(environment.get(default="1234", "TELNET_PORT"))
settings.server.timeout := -1.
settings.harbor.bind_addrs := ["127.0.0.1"]
# Keep our routing fields in the output metadata, otherwise "radio.metadata" shows only artist/title
# and the server cannot tell which request is on air after a restart.
settings.encoder.metadata.export := ["artist", "title", "album", "rt_kind", "rt_item", "track_id"]

harbor_port = int_of_string(environment.get(default="8001", "HARBOR_PORT"))
spare_list = environment.get("SPARE_PLAYLIST")
api_url = environment.get("RT_API_URL")
api_key = environment.get("RT_API_KEY")

userq = request.queue(id="userq")
autoq = request.queue(id="autoq")

# The spare: when nothing new can be downloaded (yt-dlp broken, YouTube down, the server itself down), cached
# tracks keep the air alive. The server rewrites the list (liked and often played tracks first) and liquidsoap
# picks it up by itself. Every line carries rt_kind="spare" + track_id, so the server knows what is on air.
spare = playlist(id="spare", mode="randomize", reload_mode="watch", spare_list)

# Plain fallbacks, no crossfade and no fades: crossfade pre-buffers the next track and hung
# with a stuck request; fade.in/fade.out on this fallback produced pure silence (-91 dB).
# Track-sensitive: a spare track is never cut when a fresh one gets ready, it just plays to the end.
# (The server skips the spare itself when a listener's request is waiting.)
main = fallback(id="main", track_sensitive=true, [userq, autoq, spare])

silence = metadata.map(id="silence_meta", insert_missing=true, fun (_) -> [("rt_kind", "silence")], blank(id="silence"))

radio = fallback(id="radiosrc", track_sensitive=false, [main, silence])

def notify(m) =
  try
    ignore(http.post(
      headers=[("Content-Type", "application/json"), ("X-Api-Key", api_key)],
      data=json.stringify(m),
      timeout=5.,
      "#{api_url}/api/liq/track"))
  catch err do
    log(level=3, "notify failed: #{err}")
  end
end

radio.on_track(synchronous=false, notify)

# The Windows build has no %mp3 (lame) encoder of its own; ffmpeg's libmp3lame gives the same 192 kbit/s stream.
# chunk: the Windows build leaks native memory on every write to a listener — with the default 1 KB chunks ~40 MB a
# minute per listener (measured 30.09.2026), with 64 KB (~2.7 s of audio per write) ~20 times less. start.ps1's
# watchdog restarts liquidsoap when it grows too big anyway (quietly, when nobody listens).
# burst must be bigger than chunk: with the default burst (65534) < chunk a new listener got nothing until the next
# full chunk — «Врубити» waited 1–5 s.
# burst must also outlast TWO chunks: harbor wakes every listener once per chunk, but a listener whose own buffer is
# a hair short of chunk at that moment waits for the next wake-up, so data comes 2.7 s apart and, every 15–25 s,
# 5.4 s apart (measured 09.10). With burst=131072 (5.46 s) the player's reserve fell to −0.07 s on those gaps and the
# air stuttered for 1–3 s («радіо зависає»). 192 KB = 8.2 s: reserve ≥ +2.7 s for every listener, the same leak, the
# air is 8 s behind live (Site:StreamDelaySeconds).
air = output.harbor(
  id="radio",
  %ffmpeg(format="mp3", %audio(codec="libmp3lame", b="192k", samplerate=44100, channels=2)),
  port=harbor_port, mount="radio.mp3", chunk=65536, burst=196608,
  encoding="UTF-8",
  headers=[("icy-name", "Hlechyky"), ("icy-description", "Spilne radio dlia svoikh"), ("Cache-Control", "no-cache")],
  radio)

# Who listens: the server asks "listeners" instead of reading Icecast's status page.
listeners = ref(0)
air.on_connect(synchronous=true, fun (_) -> listeners := listeners() + 1)
air.on_disconnect(synchronous=true, fun (_) -> listeners := max(0, listeners() - 1))
server.register("listeners", fun (_) -> string(listeners()))
