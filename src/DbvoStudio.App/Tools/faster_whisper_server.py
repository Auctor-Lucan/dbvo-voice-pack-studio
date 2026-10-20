"""
faster-whisper helper for DBVO Voice Pack Studio's WAV to LIP tool.

Run by the app with the Python of your own faster-whisper environment (for example
WAV_to_LIP_App\.venv\Scripts\python.exe). It loads the model once with the same settings and
fallback rules as the WAV_to_LIP app (beam size 5, no VAD filter, CUDA -> CPU/int8 retry), then
transcribes one file per request. Requests and answers are JSON lines on stdin / stdout.
"""
import json
import sys
import traceback


def send(obj):
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def main():
    cfg = json.loads(sys.argv[1])
    try:
        from faster_whisper import WhisperModel
    except Exception as exc:  # noqa: BLE001
        send({"type": "fatal", "message": f"faster-whisper is not installed in this Python environment: {exc}"})
        return

    def load(device, compute):
        send({"type": "log", "message": f"Loading faster-whisper {cfg['model']} ({device}/{compute})"})
        return WhisperModel(cfg["model"], device=device, compute_type=compute)

    try:
        try:
            model = load(cfg["device"], cfg["compute"])
            active = (cfg["device"], cfg["compute"])
        except Exception as gpu_exc:  # noqa: BLE001
            if cfg["device"] != "cuda" or not cfg["fallback"]:
                raise
            send({"type": "log", "message": f"CUDA initialization failed: {gpu_exc}"})
            send({"type": "log", "message": "Retrying model on CPU/int8."})
            model = load("cpu", "int8")
            active = ("cpu", "int8")
    except Exception as exc:  # noqa: BLE001
        send({"type": "fatal", "message": f"{exc}\n{traceback.format_exc()}"})
        return

    send({"type": "ready", "device": f"{active[0]}/{active[1]}"})
    language = None if cfg["language"] == "auto" else cfg["language"]

    for raw in sys.stdin:
        raw = raw.strip()
        if not raw:
            continue
        req = json.loads(raw)
        if req.get("quit"):
            break
        wav = req["file"]
        try:
            try:
                segments, info = model.transcribe(wav, language=language, beam_size=5, vad_filter=False)
                text = " ".join(seg.text.strip() for seg in segments).strip()
            except Exception as transcribe_exc:  # noqa: BLE001
                # CUDA can fail on first inference even if model loading succeeded.
                if active[0] != "cuda" or not cfg["fallback"]:
                    raise
                send({"type": "log", "message": f"CUDA inference failed on {wav}: {transcribe_exc}"})
                send({"type": "log", "message": "Retrying this file with CPU/int8."})
                model = load("cpu", "int8")
                active = ("cpu", "int8")
                segments, info = model.transcribe(wav, language=language, beam_size=5, vad_filter=False)
                text = " ".join(seg.text.strip() for seg in segments).strip()
            send({"type": "result", "text": text, "device": f"{active[0]}/{active[1]}"})
        except Exception as exc:  # noqa: BLE001
            send({"type": "error", "message": str(exc), "device": f"{active[0]}/{active[1]}"})


if __name__ == "__main__":
    main()
