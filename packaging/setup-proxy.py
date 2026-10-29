from pathlib import Path
import hashlib, shutil, subprocess, sys, urllib.request, zipfile
root=Path(__file__).resolve().parent.parent
target=root/'artifacts/runtime/Proxy'; target.mkdir(parents=True,exist_ok=True)
cache=root/'artifacts/dependencies'; cache.mkdir(parents=True,exist_ok=True)
archive=cache/'python-3.13.17-embed-amd64.zip'
if not archive.exists():
    urllib.request.urlretrieve('https://www.python.org/ftp/python/3.13.17/python-3.13.17-embed-amd64.zip',archive)
if hashlib.sha256(archive.read_bytes()).hexdigest()!='9e35319bffedab8b36ad44d8f377cb4f1733bcec257a0b6d04d94f01486ddb38':
    raise RuntimeError('Python runtime checksum mismatch')
runtime=target/'runtime'; runtime.mkdir(exist_ok=True)
with zipfile.ZipFile(archive) as z:z.extractall(runtime)
(runtime/'python313._pth').write_text('python313.zip\n.\n..\n..\\packages\n',encoding='ascii')
for name in ('proxy','utils'):
    shutil.copytree(root/'Proxy'/name,target/name,dirs_exist_ok=True,ignore=shutil.ignore_patterns('__pycache__'))
for name in ('runner.py','requirements.txt','NOTICE.txt','LICENSE-TgWsProxy.txt'):
    shutil.copy2(root/'Proxy'/name,target/name)
wheels=cache/'wheels'; wheels.mkdir(exist_ok=True)
subprocess.run([sys.executable,'-m','pip','download','--disable-pip-version-check','--only-binary=:all:','--platform','win_amd64','--python-version','3.13','--implementation','cp','--abi','cp313','--abi','abi3','--dest',str(wheels),'-r',str(root/'Proxy/requirements.txt')],check=True)
packages=target/'packages'; packages.mkdir(exist_ok=True)
for wheel in wheels.glob('*.whl'):
    with zipfile.ZipFile(wheel) as z:z.extractall(packages)
print('Proxy runtime ready')
