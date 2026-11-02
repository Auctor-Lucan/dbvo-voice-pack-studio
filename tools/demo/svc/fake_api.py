import os
# Stand-in for the ElevenLabs API and an OpenAI-compatible chat endpoint, for the demo recording.
import json, random, re, subprocess, time, threading
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
ThreadingHTTPServer.request_queue_size = 128
ThreadingHTTPServer.daemon_threads = True
from urllib.parse import urlparse, parse_qs

used = 1_224_000
lock = threading.Lock()
VOICES = [
    {"voice_id": "sample01", "name": "Sample Voice", "category": "professional"},
    {"voice_id": "sample02", "name": "Second Voice", "category": "professional"},
    {"voice_id": "smith01", "name": "Smith (example)", "category": "professional"},
    {"voice_id": "narr01", "name": "Old Nord Narrator", "category": "generated"},
    {"voice_id": "pre001", "name": "Rachel", "category": "premade"},
    {"voice_id": "gen101", "name": "Regal Queen", "category": "generated"},
    {"voice_id": "gen102", "name": "Cold Noble", "category": "generated"},
    {"voice_id": "gen103", "name": "Stern Priestess", "category": "generated"},
    {"voice_id": "gen104", "name": "Gruff Captain", "category": "generated"},
]
MODELS = [
    {"model_id": "eleven_v4", "name": "Eleven v4", "can_do_text_to_speech": True, "can_use_style": False, "can_use_speaker_boost": False, "model_rates": {"character_cost_multiplier": 0.5, "cost_discount_multiplier": 1}},
    {"model_id": "eleven_v3", "name": "Eleven v3", "can_do_text_to_speech": True, "can_use_style": False, "can_use_speaker_boost": False},
    {"model_id": "eleven_multilingual_v2", "name": "Eleven Multilingual v2", "can_do_text_to_speech": True, "can_use_style": True, "can_use_speaker_boost": True},
    {"model_id": "eleven_flash_v2_5", "name": "Eleven Flash v2.5", "can_do_text_to_speech": True, "can_use_style": False, "can_use_speaker_boost": True},
    {"model_id": "eleven_multilingual_sts_v2", "name": "Eleven Multilingual v2 (STS)", "can_do_text_to_speech": False, "can_do_voice_conversion": True},
]

def audio(seconds, fmt):
    if os.environ.get("PEAKY"):
        import numpy as np
        sr=44100; t=np.arange(int(seconds*sr))/sr
        x=0.08*np.sin(2*np.pi*(180+random.randint(0,60))*t)*(0.6+0.4*np.sin(2*np.pi*3*t))
        for c in np.arange(0.3, seconds-0.05, 0.45):   # hard consonant bursts, about 20 dB above the speech
            i=int(c*sr); n=int(0.004*sr); x[i:i+n]+=0.85*np.random.uniform(-1,1,n)*np.hanning(n)
        pcm=(np.clip(x,-1,1)*32767).astype(np.int16).tobytes()
        args=["-ar", fmt.split("_")[1], "-f", "s16le", "-"] if fmt.startswith("pcm_") else ["-b:a","128k","-f","mp3","-"]
        return subprocess.run(["ffmpeg","-v","quiet","-f","s16le","-ar","44100","-ac","1","-i","-","-ac","1"]+args, input=pcm, capture_output=True).stdout
    # A soft, speech-length tone; the video is silent, the files only need to be real audio.
    f = 180 + random.randint(0, 60)
    src = f"sine=frequency={f}:duration={seconds:.2f}:sample_rate=44100"
    if fmt.startswith("pcm_"):
        rate = fmt.split("_")[1]
        return subprocess.run(["ffmpeg", "-v", "quiet", "-f", "lavfi", "-i", src, "-ar", rate, "-ac", "1", "-f", "s16le", "-"], capture_output=True).stdout
    return subprocess.run(["ffmpeg", "-v", "quiet", "-f", "lavfi", "-i", src, "-af", "volume=0.2", "-ac", "1", "-b:a", "128k", "-f", "mp3", "-"], capture_output=True).stdout

TAGS = [("blood|kill|die|enemy|fight", "[menacing]"), ("\\?$", "[curious]"), ("ha|joke|fun|play", "[laughs]"), ("sorry|alone|miss", "[softly]"), ("!", "[excited]")]

class H(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def log_message(self, *a): pass
    def send(self, code, body, ctype="application/json"):
        if isinstance(body, (dict, list)): body = json.dumps(body).encode()
        self.send_response(code); self.send_header("Content-Type", ctype)
        if getattr(self, "_cc", None) is not None: self.send_header("character-cost", str(self._cc)); self._cc = None
        self.send_header("X-Dummy", "1"); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def do_GET(self):
        p = urlparse(self.path).path
        if p.endswith("/v1/voices"): return self.send(200, {"voices": VOICES})
        if p.endswith("/v1/models"): return self.send(200, MODELS)
        if p.endswith("/v1/user/subscription"):
            with lock: u = used
            return self.send(200, {"tier": "pro", "character_count": u, "character_limit": 2_450_000, "next_character_count_reset_unix": int(time.time()) + 12 * 86400})
        self.send(404, {"detail": "not found"})
    def do_POST(self):
        global used
        u = urlparse(self.path); p = u.path
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            raw = b""
            while True:
                size = int(self.rfile.readline().strip().split(b";")[0], 16)
                if size == 0: self.rfile.readline(); break
                raw += self.rfile.read(size); self.rfile.readline()
        else:
            n = int(self.headers.get("Content-Length") or 0); raw = self.rfile.read(n)
        if "/v1/text-to-speech/" in p:
            body = json.loads(raw)
            text = re.sub(r"\[[^\]]*\]", "", body.get("text", "")).strip()
            fmt = parse_qs(u.query).get("output_format", ["mp3_44100_128"])[0]
            secs = max(0.7, 0.062 * len(text) + 0.35 + random.uniform(-0.1, 0.25))
            time.sleep(random.uniform(0.7, 1.6))
            with lock: used += len(body.get("text", ""))
            open("/tmp/dbvo-work/demo/out/tts_log.jsonl","a").write(json.dumps({"text": body.get("text"), "previous_text": body.get("previous_text")})+"\n")
            self._cc = len(body.get("text", ""))
            return self.send(200, audio(secs, fmt), "audio/mpeg")
        if "/v1/speech-to-speech/" in p:
            fmt = parse_qs(u.query).get("output_format", ["mp3_44100_128"])[0]
            time.sleep(random.uniform(0.8, 1.4))
            return self.send(200, audio(1.8, fmt), "audio/mpeg")
        if p.endswith("/chat/completions"):
            body = json.loads(raw)
            if "proofread" in body["messages"][0]["content"]:
                import sys as _s; _s.path.insert(0, "/tmp/dbvo-work/demo/svc"); import fake_ai
                time.sleep(float(__import__("os").environ.get("AI_DELAY", "0.6")))
                return self.send(200, {"choices": [{"message": {"role": "assistant", "content": fake_ai.proofread(body["messages"][-1]["content"])}}]})
            line = body["messages"][-1]["content"].split("Line: ", 1)[-1].strip()
            tag = next((t for pat, t in TAGS if re.search(pat, line, re.I)), "[calm]")
            time.sleep(random.uniform(0.3, 0.9))
            return self.send(200, {"choices": [{"message": {"role": "assistant", "content": f"{tag} {line}"}}]})
        self.send(404, {"detail": "not found"})

threading.Thread(target=ThreadingHTTPServer(("127.0.0.1", 11434), H).serve_forever, daemon=True).start()
ThreadingHTTPServer(("127.0.0.1", 8931), H).serve_forever()
