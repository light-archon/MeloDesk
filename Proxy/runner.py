"""Local-only stdin-controlled adapter for unmodified Flowseal TG WS Proxy."""
import asyncio
import json
import logging
import sys
import threading
import ipaddress

logging.disable(logging.CRITICAL)  # Upstream startup logs contain the proxy secret.

def emit(event):
    print(json.dumps({'event': event}), flush=True)

async def serve(config):
    from proxy import tg_ws_proxy as core
    from proxy.config import proxy_config
    proxy_config.host = '127.0.0.1'
    proxy_config.port = config['port']
    proxy_config.secret = config['secret']
    proxy_config.dc_redirects = {int(k): str(ipaddress.ip_address(v)) for k, v in config['dc'].items() if v}
    proxy_config.fallback_cfproxy = config['fallback']
    proxy_config.pool_size = 2
    proxy_config.disable_secure = False
    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    def watch_parent():
        for line in sys.stdin:
            if line.strip() == 'stop':
                break
        loop.call_soon_threadsafe(stop.set)
    threading.Thread(target=watch_parent,daemon=True).start()
    task = asyncio.create_task(core._run(stop))
    while core._server_instance is None and not task.done():
        await asyncio.sleep(.05)
    if core._server_instance is not None:
        emit('ready')
    await task

def main():
    config=json.loads(sys.stdin.readline(8192))
    if type(config.get('port')) is not int or not 1024 <= config['port'] <= 65535:
        raise ValueError('port')
    secret=config.get('secret','')
    if not isinstance(secret,str) or len(secret) != 32 or len(bytes.fromhex(secret)) != 16:
        raise ValueError('secret')
    if type(config.get('fallback')) is not bool or not isinstance(config.get('dc'),dict) or set(config['dc']) - {'2','4'}:
        raise ValueError('config')
    asyncio.run(serve(config))

if __name__ == '__main__':
    try:
        main()
    except (Exception, KeyboardInterrupt):
        emit('error')
        sys.exit(1)
