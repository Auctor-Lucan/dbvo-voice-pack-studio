# Fake OpenAI-compatible chat endpoint (port 11434) for the proofreading demo/tests.
import json, re, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
DELAY = float(sys.argv[1]) if len(sys.argv) > 1 else 0.4
FIXES = [(r"\btrustworty\b", "trustworthy", "spelling", "misspelled"), (r"\bDont\b", "Don't", "spelling", "missing apostrophe"),
         (r"\bI've trying\b", "I'm trying", "grammar", "wrong verb form"), (r"\bis the often\b", "is often", "grammar", "extra word"),
         (r"\ba quite the\b", "quite the", "grammar", "extra word"), (r"  +", " ", "punctuation", "double space"),
         (r"\b(\w+) \1\b", r"\1", "grammar", "doubled word"), (r"\bwill make you\b(?=.*\bWill\b)", "make you", "grammar", "doubled will")]
def proofread(user):
    out = []
    for it in json.loads(user):
        t = it['t']; kind = None; why = ''
        if t.startswith('Will killing') and ' will make ' in t:
            t = t.replace(' will make ', ' make ', 1); kind, why = 'grammar', 'will appears twice'
        for pat, rep, k, w in FIXES:
            n = re.sub(pat, rep, t)
            if n != t: t = n; kind = kind or k; why = why or w
        if t.startswith('Khajiit does'):
            t = 'I do' + t[len('Khajiit does'):]; kind, why = 'style', 'first person'
        if t != it['t']: out.append({'i': it['i'], 't': t, 'kind': kind, 'why': why})
    return '```json\n' + json.dumps(out) + '\n```'
class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get('content-length', 0))) or b'{}')
        msgs = body.get('messages', [])
        user = msgs[-1]['content'] if msgs else ''
        sysm = msgs[0]['content'] if msgs else ''
        time.sleep(DELAY)
        if 'proofread' in sysm:
            out = []
            for it in json.loads(user):
                t = it['t']; kind = None; why = ''
                if t.startswith('Will killing') and ' will make ' in t:
                    t = t.replace(' will make ', ' make ', 1); kind, why = 'grammar', 'will appears twice'
                for pat, rep, k, w in FIXES:
                    n = re.sub(pat, rep, t)
                    if n != t: t = n; kind = kind or k; why = why or w
                if t.startswith('Khajiit does'):
                    t = 'I do' + t[len('Khajiit does'):]; kind, why = 'style', 'first person'
                if t != it['t']: out.append({'i': it['i'], 't': t, 'kind': kind, 'why': why})
            content = '```json\n' + json.dumps(out) + '\n```'
        else:
            content = user.split('Line: ')[-1]
        res = json.dumps({'choices': [{'message': {'role': 'assistant', 'content': content}}]}).encode()
        self.send_response(200); self.send_header('content-type', 'application/json'); self.send_header('content-length', str(len(res))); self.end_headers(); self.wfile.write(res)
if __name__ == '__main__': ThreadingHTTPServer(('127.0.0.1', 11434), H).serve_forever()
