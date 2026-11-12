"""Run in a fresh Python process against RID-neutral publish output (needs pythonnet).

Usage: python tests/packaging/sqlite_smoke.py INTEGRATION_DIR [DOTNET_ROOT]
This deliberately uses Python.NET, not dotnet exec, to catch missing native probing.
"""
import os
from pathlib import Path
import sys
import tempfile
import sqlite3

root = Path(sys.argv[1]).resolve()
out = root / 'out'
# Releases serve both glibc and Alpine, on each supported Linux architecture.
for rid, machine, bits in [('linux-x64', 62, 2), ('linux-arm64', 183, 2),
                           ('linux-arm', 40, 1), ('linux-musl-x64', 62, 2),
                           ('linux-musl-arm64', 183, 2), ('linux-musl-arm', 40, 1)]:
    binary = out / 'runtimes' / rid / 'native' / 'libe_sqlite3.so'
    header = binary.read_bytes()[:20]
    assert header[:4] == b'\x7fELF' and header[4] == bits, binary
    assert int.from_bytes(header[18:20], 'little') == machine, binary

os.environ['DOTNET_SYSTEM_GLOBALIZATION_INVARIANT'] = 'true'
from pythonnet import load
kwargs = {'dotnet_root': sys.argv[2]} if len(sys.argv) > 2 else {}
load('coreclr', runtime_config=str(root / 'dotnet.runtimeconfig.json'), **kwargs)
sys.path.insert(0, str(out))
import clr
clr.AddReference('HassSharp')
from HassSharp import RunHistoryStore
from System.Runtime.InteropServices import RuntimeInformation

with tempfile.TemporaryDirectory() as folder:
    database = str(Path(folder) / 'history.db')
    store = RunHistoryStore(database, 100)
    identity = store.Identity('smoke.cs')
    assert identity
    assert store.ClaimOccurrence('smoke-occurrence')
    reopened = RunHistoryStore(database, 100)
    assert reopened.Identity('smoke.cs') == identity
    assert not reopened.ClaimOccurrence('smoke-occurrence')
    with sqlite3.connect(database) as connection:
        assert connection.execute('SELECT count(*) FROM __EFMigrationsHistory').fetchone()[0] == 2
print(f'PASS: packaged SQLite load, migrations, identity persistence, occurrence deduplication ({RuntimeInformation.RuntimeIdentifier})')
