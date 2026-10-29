"""MeloDesk promotion publisher. Public read-only origin and separate loopback admin."""
import argparse,base64,hashlib,hmac,json,os,re,secrets,signal,subprocess,threading,time
from http.server import BaseHTTPRequestHandler,ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

MAX_IMAGE=2*1024*1024
MAX_BODY=3*1024*1024
def image_type(data):
    if data.startswith(b'\x89PNG\r\n\x1a\n'): return 'image/png'
    if data.startswith(b'\xff\xd8\xff'): return 'image/jpeg'
    if data[:4]==b'RIFF' and data[8:12]==b'WEBP': return 'image/webp'
    raise ValueError('Выбери изображение PNG, JPEG или WebP.')

def validate(payload):
    if not isinstance(payload,dict): raise ValueError('Ожидаются поля баннера.')
    result={key:str(payload.get(key,'')).strip() for key in ('title','text','url','button','secondaryUrl','secondaryButton')}
    result['enabled']=payload.get('enabled') is True
    result['upcoming']=payload.get('upcoming') is True
    result['button']=result['button'] or 'Открыть канал'
    result['secondaryButton']=result['secondaryButton'] or 'Наш бот'
    for key,maximum in [('title',80),('text',240),('url',256),('button',32),('secondaryUrl',256),('secondaryButton',32)]:
        if len(result[key])>maximum or any(ord(c)<32 for c in result[key]): raise ValueError('Проверь длину и символы в полях баннера.')
    for key in ('url','secondaryUrl'):
        if not result[key] and (key!='url' or not result['enabled'] or result['upcoming']): continue
        url=urlsplit(result[key])
        if url.scheme!='https' or url.netloc.lower()!='t.me' or not re.fullmatch(r'/(?:[A-Za-z][A-Za-z0-9_]{3,31}|\+[A-Za-z0-9_-]{8,128})',url.path) or url.query or url.fragment:
            raise ValueError('Нужна ссылка вида https://t.me/имя_канала_или_бота.')
    if result['enabled'] and not result['title']: raise ValueError('Укажи заголовок баннера.')
    return result

class Store:
    def __init__(self,root):
        self.root=Path(root); self.root.mkdir(parents=True,exist_ok=True)
        self.assets=self.root/'assets'; self.assets.mkdir(exist_ok=True)
        self.lock=threading.RLock(); self.public_url=''; self.stop=threading.Event()
        private=self.root/'admin-secret.json'
        if not private.exists(): self.atomic(private,dict(token=secrets.token_urlsafe(32)))
        self.token=json.loads(private.read_text(encoding='utf-8'))['token']
        self.settings=self.root/'banner.json'
        if not self.settings.exists(): self.atomic(self.settings,dict(enabled=False,title='MeloDeskNews',text='Новости MeloDesk, обновления и музыка. Всё о проекте — в нашем канале.',url='',button='Читать новости',secondaryUrl='',secondaryButton='Наш бот',asset='',revision=''))
    @staticmethod
    def atomic(path,value):
        temporary=path.with_suffix('.tmp'); temporary.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8'); temporary.replace(path)
    def current(self):
        with self.lock: return json.loads(self.settings.read_text(encoding='utf-8'))
    def save(self,payload):
        value=validate(payload)
        with self.lock:
            value['asset']=self.current().get('asset','')
            if payload.get('removeImage') is True: value['asset']=''
            if payload.get('image'):
                try: data=base64.b64decode(payload['image'],validate=True)
                except (ValueError,TypeError): raise ValueError('Не удалось прочитать изображение.')
                if len(data)>MAX_IMAGE: raise ValueError('Картинка должна быть не больше 2 МБ.')
                image_type(data); name=hashlib.sha256(data).hexdigest(); target=self.assets/name
                if not target.exists(): target.write_bytes(data)
                value['asset']=name
            value['revision']=hashlib.sha256(json.dumps(value,ensure_ascii=False,sort_keys=True).encode()).hexdigest()
            self.atomic(self.settings,value)
        return value
    def manifest(self):
        value=self.current()
        if not value['enabled']: return dict(Version=1,Enabled=False)
        return dict(Version=1,Enabled=value['enabled'],Revision=value['revision'],Title=value['title'],Text=value['text'],
            Url=value['url'],Button=value['button'],Upcoming=value.get('upcoming',False),SecondaryUrl=value.get('secondaryUrl',''),SecondaryButton=value.get('secondaryButton','Наш бот'),Image='/assets/'+value['asset'] if value['asset'] else '')

def handler(store,admin=False,admin_port=18741):
    class Handler(BaseHTTPRequestHandler):
        server_version='MeloDesk'
        def log_message(self,*args): pass
        def send(self,status,body=b'',content='application/json; charset=utf-8',cache='no-store'):
            if not isinstance(body,bytes): body=json.dumps(body,ensure_ascii=False).encode('utf-8')
            self.send_response(status); self.send_header('Content-Type',content); self.send_header('Content-Length',str(len(body)))
            self.send_header('Cache-Control',cache); self.send_header('X-Content-Type-Options','nosniff'); self.send_header('Referrer-Policy','no-referrer')
            if admin: self.send_header('Content-Security-Policy',"default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'")
            self.end_headers(); self.wfile.write(body)
        def allowed(self):
            origin=self.headers.get('Origin','')
            hosts=(f'127.0.0.1:{admin_port}',f'localhost:{admin_port}')
            return self.headers.get('Host') in hosts and (not origin or origin in ('http://'+h for h in hosts)) and hmac.compare_digest(self.headers.get('Authorization',''),'Bearer '+store.token)
        def do_GET(self):
            path=urlsplit(self.path).path
            if admin:
                if path=='/': return self.send(200,(Path(__file__).with_name('panel.html')).read_bytes(),'text/html; charset=utf-8')
                if not self.allowed(): return self.send(403,dict(error='Нет доступа к панели. Открой её через ярлык на этом ПК.'))
                if path=='/admin/state': return self.send(200,dict(banner=store.current(),publicUrl=store.public_url,feedUrl=store.public_url+'/v1/banner' if store.public_url else '',maxImageBytes=MAX_IMAGE))
                if path=='/admin/image':
                    asset=store.current().get('asset','')
                    if not asset: return self.send(404)
                    data=(store.assets/asset).read_bytes(); return self.send(200,data,image_type(data))
                return self.send(404)
            if path=='/health': return self.send(200,dict(service='MeloDesk promotions',version=1))
            if path=='/v1/banner': return self.send(200,store.manifest())
            if re.fullmatch(r'/assets/[a-f0-9]{64}',path):
                file=store.assets/path.rsplit('/',1)[1]
                if file.is_file():
                    data=file.read_bytes(); return self.send(200,data,image_type(data),'public, max-age=31536000, immutable')
            self.send(404,dict(error='Not found'))
        def do_POST(self):
            if not admin: return self.send(405)
            if not self.allowed(): return self.send(403)
            try:
                length=int(self.headers.get('Content-Length','0'))
                if length<=0 or length>MAX_BODY: return self.send(413)
                payload=json.loads(self.rfile.read(length))
                if self.path=='/admin/banner': return self.send(200,dict(banner=store.save(payload)))
                if self.path=='/admin/stop': self.send(200,dict(stopping=True)); store.stop.set(); return
                self.send(404)
            except (ValueError,TypeError,UnicodeError) as error: self.send(400,dict(error=str(error)))
    return Handler

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--root',default=str(Path(os.environ['LOCALAPPDATA'])/'MusiGramAds'))
    parser.add_argument('--cloudflared'); parser.add_argument('--public-port',type=int,default=18740); parser.add_argument('--admin-port',type=int,default=18741)
    parser.add_argument('--public-url'); parser.add_argument('--existing-tunnel-pid',type=int)
    args=parser.parse_args(); store=Store(args.root)
    public=ThreadingHTTPServer(('127.0.0.1',args.public_port),handler(store))
    admin=ThreadingHTTPServer(('127.0.0.1',args.admin_port),handler(store,True,args.admin_port))
    for server in (public,admin): threading.Thread(target=server.serve_forever,daemon=True).start()
    panel=f'http://127.0.0.1:{args.admin_port}/#'+store.token
    (store.root/'Панель рекламы.url').write_text('[InternetShortcut]\nURL='+panel+'\n',encoding='utf-8')
    runtime=store.root/'runtime.json'
    def status(**extra):
        value=dict(pid=os.getpid(),adminPort=args.admin_port,publicPort=args.public_port,publicUrl=store.public_url)
        if args.existing_tunnel_pid: value['tunnelPid']=args.existing_tunnel_pid
        value.update(extra); Store.atomic(runtime,value)
    tunnel=None; status(stage='Starting')
    try:
        if args.public_url:
            parsed=urlsplit(args.public_url)
            if parsed.scheme!='https' or not re.fullmatch(r'[a-z0-9-]+\.trycloudflare\.com',parsed.netloc) or parsed.path not in ('','/'):
                raise ValueError('Invalid existing public tunnel URL.')
            store.public_url=args.public_url.rstrip('/'); status(stage='Online',tunnelPid=args.existing_tunnel_pid)
        elif args.cloudflared:
            logfile=store.root/'tunnel.log'
            log=logfile.open('w',encoding='utf-8')
            tunnel=subprocess.Popen([args.cloudflared,'tunnel','--no-autoupdate','--edge-ip-version','4','--protocol','http2','--url',f'http://127.0.0.1:{args.public_port}'],stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
            for _ in range(120):
                urls=re.findall(r'https://[a-z0-9-]+\.trycloudflare\.com',logfile.read_text(encoding='utf-8',errors='replace'))
                if urls: store.public_url=urls[0]; break
                if tunnel.poll() is not None: break
                time.sleep(.5)
            status(stage='Online' if store.public_url else 'LocalOnly',tunnelPid=tunnel.pid)
        else: status(stage='LocalOnly')
        while not store.stop.wait(1):
            if tunnel and tunnel.poll() is not None: status(stage='TunnelStopped'); tunnel=None
    finally:
        for server in (public,admin): server.shutdown(); server.server_close()
        if tunnel:
            tunnel.terminate()
            try: tunnel.wait(timeout=10)
            except subprocess.TimeoutExpired: tunnel.kill()
        status(stage='Stopped')

if __name__=='__main__': main()
