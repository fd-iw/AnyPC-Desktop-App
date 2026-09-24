# AnyPC wire protocol (v1)

The desktop app listens on **TCP 47800** and serves a single TLS WebSocket endpoint:

```
wss://<pc-ip>:47800/anypc
```

The TLS certificate is self-signed and generated on first run. Clients **pin** the
SHA-256 fingerprint of the certificate's DER bytes (lower-case hex, no separators).
The fingerprint is shown in the desktop window, embedded in the pairing QR code and
published in the Bonjour TXT record.

## Discovery

Bonjour / mDNS service type `_anypc._tcp`, port 47800. TXT record:

| key  | value                                   |
|------|-----------------------------------------|
| `id` | stable server id (32 hex chars)         |
| `fp` | certificate fingerprint                 |
| `v`  | protocol version (`1`)                  |

## Pairing QR code

```
anypc://pair?h=<ip>[,<ip>...]&p=<port>&fp=<fingerprint>&id=<serverId>&pin=<pin>&n=<url-encoded name>
```

## Messages

Text frames are UTF-8 JSON objects with a `t` (type) field. Binary frames start
with a 1-byte type.

### Handshake

| direction | message | notes |
|-----------|---------|-------|
| C→S | `{"t":"hello","v":1,"deviceId":"…","deviceName":"…","token":"…"?}` | first message; `token` omitted if never paired |
| S→C | `{"t":"need_pair","serverId":"…","serverName":"…"}` | token missing or invalid |
| C→S | `{"t":"pair","deviceId":"…","deviceName":"…","pin":"123456"}` | |
| S→C | `{"t":"pair_fail","reason":"bad_pin"\|"locked","retryAfter":<sec>}` | |
| S→C | `{"t":"paired","token":"…"}` | followed by `welcome` |
| S→C | `{"t":"welcome","v":1,"serverId":"…","serverName":"…","monitors":[{"i":0,"name":"…","x":0,"y":0,"w":1920,"h":1080,"primary":true}]}` | authenticated |

A session that has not authenticated within 30 s is closed. Five wrong PINs lock
pairing for 60 s (doubling on each further lockout).

### Screen

| direction | message |
|-----------|---------|
| C→S | `{"t":"stream","on":true,"monitor":0,"maxWidth":1136,"quality":60,"fps":20}` |
| C→S | `{"t":"ack","seq":<n>}` — sent after each frame is displayed |
| S→C | binary `0x01` frame |

Frame layout (big-endian):

```
u8  0x01
u32 seq
u16 width
u16 height
u16 cursorX   (frame pixels, 0xFFFF = cursor not on this monitor)
u16 cursorY
…   JPEG bytes
```

At most 2 frames are un-acked at any time. Frames are only sent when the screen changed
(a keep-alive frame is sent every 2 s).

### Input

| message | meaning |
|---------|---------|
| `{"t":"move","x":0.5,"y":0.5}` | absolute, normalized 0–1 within the streamed monitor |
| `{"t":"moverel","dx":10,"dy":-4}` | relative, in PC pixels |
| `{"t":"button","b":"left"\|"right"\|"middle","a":"click"\|"dblclick"\|"down"\|"up"}` | |
| `{"t":"wheel","dx":0,"dy":120}` | wheel delta (120 = one notch; positive dy = scroll up) |
| `{"t":"key","vk":13,"a":"press"\|"down"\|"up","mods":["ctrl","alt","shift","win"]}` | Windows virtual-key code |
| `{"t":"text","s":"hello"}` | Unicode text |

### Files

| direction | message |
|-----------|---------|
| C→S | `{"t":"fs_list","id":1,"path":""}` — empty path lists drives |
| S→C | `{"t":"fs_list","id":1,"path":"C:\\","parent":"","entries":[{"n":"Users","p":"C:\\Users","d":true,"s":0,"m":1700000000}]}` — `p` is the full path to request next |
| C→S | `{"t":"fs_get","id":2,"path":"C:\\a.txt"}` |
| S→C | `{"t":"fs_meta","id":2,"name":"a.txt","size":123}` then binary `0x02` chunks then `{"t":"fs_done","id":2}` |
| C→S | `{"t":"fs_put","id":3,"dir":"C:\\Users\\me\\Downloads","name":"photo.jpg","size":1234}` |
| S→C | `{"t":"fs_ready","id":3}` |
| C→S | binary `0x03` chunks then `{"t":"fs_put_end","id":3}` |
| S→C | `{"t":"fs_done","id":3,"name":"photo.jpg"}` |
| C→S | `{"t":"fs_cancel","id":2}` |
| S→C | `{"t":"fs_err","id":2,"msg":"…"}` |

Binary chunk layout: `u8 type (0x02 / 0x03)`, `u32 id`, then data (≤ 64 KiB).

### System

| direction | message |
|-----------|---------|
| C→S | `{"t":"sys","a":"lock"\|"sleep"\|"restart"\|"shutdown"\|"signout"\|"volume_up"\|"volume_down"\|"mute"\|"play_pause"\|"next_track"\|"prev_track"\|"show_desktop"\|"task_manager"}` |
| S→C | `{"t":"sys_ok","a":"…"}` |
| C→S | `{"t":"ping","ts":<n>}` → S→C `{"t":"pong","ts":<n>}` |
| S→C | `{"t":"error","msg":"…"}` |
