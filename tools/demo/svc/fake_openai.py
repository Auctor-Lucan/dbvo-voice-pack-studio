import http.server, json, struct, math, sys
LOG = '/tmp/dbvo-work/demo/out/openai_log.jsonl'
def wav(sec=0.8, rate=24000):
    n = int(sec*rate); data = b''.join(struct.pack('<h', int(8000*math.sin(2*math.pi*180*i/rate))) for i in range(n))
    return b'RIFF'+struct.pack('<I',36+len(data))+b'WAVEfmt '+struct.pack('<IHHIIHH',16,1,1,rate,rate*2,2,16)+b'data'+struct.pack('<I',len(data))+data
class H(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        open(LOG,'a').write(json.dumps({'path': self.path, 'auth': self.headers.get('Authorization'), 'body': body})+'\n')
        w = wav(); self.send_response(200); self.send_header('Content-Type','audio/wav'); self.send_header('Content-Length',str(len(w))); self.end_headers(); self.wfile.write(w)
    def log_message(self,*a): pass
http.server.HTTPServer(('127.0.0.1', 5123), H).serve_forever()
