import os
import shutil
import subprocess
import sys
import glob
import json
import re

local_app_data = os.environ.get('LOCALAPPDATA', '')
import tempfile
temp_dir = tempfile.gettempdir()

src_leveldb = os.path.join(
    local_app_data,
    r'Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\WV2Profile_tfw\IndexedDB\https_teams.microsoft.com_0.indexeddb.leveldb'
)

copy_dst = os.path.join(temp_dir, 'teams_copy_fresh')
out_dst = os.path.join(temp_dir, 'teams-extraido')

print(f"Buscando base de Teams en: {src_leveldb}")
if not os.path.exists(src_leveldb):
    print("No se encontró la ruta directa. Buscando alternativas...")
    pkg_dir = os.path.join(local_app_data, r'Packages\MSTeams_8wekyb3d8bbwe')
    found = glob.glob(f"{pkg_dir}/**/https_teams.microsoft.com_0.indexeddb.leveldb", recursive=True)
    if found:
        src_leveldb = found[0]
        print(f"Encontrada en: {src_leveldb}")
    else:
        print("ERROR: No se encontró la base de datos de Teams.")
        sys.exit(1)

# Copiar archivos ignorando locks
os.makedirs(copy_dst, exist_ok=True)
copied = 0
for item in os.listdir(src_leveldb):
    s = os.path.join(src_leveldb, item)
    d = os.path.join(copy_dst, item)
    if os.path.isfile(s):
        try:
            shutil.copy2(s, d)
            copied += 1
        except Exception as e:
            # LOCK u otros archivos bloqueados
            pass

print(f"Archivos copiados a {copy_dst}: {copied}")

# Correr extraer.py
extraer_script = os.path.join(os.path.dirname(os.path.abspath(__file__)), "extraer.py")
print(f"Ejecutando {extraer_script}...")
res = subprocess.run([sys.executable, extraer_script, copy_dst, out_dst], capture_output=True, text=True, encoding='utf-8', errors='replace')
print("Salida de extraer.py:")
print(res.stdout)
if res.stderr:
    print("Stderr:", res.stderr)

jsonl_file = os.path.join(out_dst, "mensajes.jsonl")
print(f"¿Existe {jsonl_file}?: {os.path.exists(jsonl_file)}")
