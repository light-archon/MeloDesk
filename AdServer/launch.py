from pathlib import Path
import ctypes,json,os,subprocess,sys,time,urllib.request,webbrowser

def tunnel_alive(pid,exe):
    if not isinstance(pid,int): return False
    kernel=ctypes.WinDLL('kernel32',use_last_error=True); kernel.OpenProcess.restype=ctypes.c_void_p
    kernel.QueryFullProcessImageNameW.argtypes=[ctypes.c_void_p,ctypes.c_uint32,ctypes.c_wchar_p,ctypes.POINTER(ctypes.c_uint32)]
    kernel.CloseHandle.argtypes=[ctypes.c_void_p]
    handle=kernel.OpenProcess(0x1000,False,pid)
    if not handle: return False
    try:
        buffer=ctypes.create_unicode_buffer(32768); size=ctypes.c_uint32(len(buffer))
        return bool(kernel.QueryFullProcessImageNameW(handle,0,buffer,ctypes.byref(size))) and Path(buffer.value).resolve()==Path(exe).resolve()
    finally: kernel.CloseHandle(handle)

def main():
    app=Path(__file__).parent; root=Path(os.environ['LOCALAPPDATA'])/'MusiGramAds'; runtime=root/'runtime.json'
    def alive():
        try:
            state=json.loads(runtime.read_text(encoding='utf-8'))
            with urllib.request.urlopen('http://127.0.0.1:'+str(state['publicPort'])+'/health',timeout=1) as reply:
                return json.load(reply).get('service')in ('MeloDesk promotions','MusiGram promotions')
        except Exception: return False
    if not alive():
        python=app/'python/pythonw.exe'; tunnel=app/'cloudflared.exe'
        arguments=[str(python),str(app/'server.py'),'--cloudflared',str(tunnel),'--root',str(root)]
        try:
            previous=json.loads(runtime.read_text(encoding='utf-8'))
            if tunnel_alive(previous.get('tunnelPid'),tunnel) and previous.get('publicUrl'):
                arguments+=['--public-url',previous['publicUrl'],'--existing-tunnel-pid',str(previous['tunnelPid'])]
        except (OSError,ValueError): pass
        subprocess.Popen(arguments,cwd=str(app),creationflags=subprocess.CREATE_NO_WINDOW)
        for _ in range(60):
            if alive(): break
            time.sleep(.25)
    shortcut=root/'Панель рекламы.url'
    if shortcut.exists():
        url=next(line[4:] for line in shortcut.read_text(encoding='utf-8').splitlines() if line.startswith('URL='))
        webbrowser.open(url)

if __name__=='__main__': main()
