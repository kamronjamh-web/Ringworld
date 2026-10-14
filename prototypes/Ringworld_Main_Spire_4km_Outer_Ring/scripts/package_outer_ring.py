"""Package one final editable outer-ring asset and its three inspection views."""
from pathlib import Path
import hashlib,json,zipfile
ROOT=Path(__file__).resolve().parent.parent
report=json.loads((ROOT/'validation/geometry_validation.json').read_text())
assert report['overall_pass'] is True
for key,name in [('asset','Main_Spire_Outer_Ring.blend'),('glb','Main_Spire_Outer_Ring_Godot.glb')]:
    assert report['sha256'][key]==hashlib.sha256((ROOT/name).read_bytes()).hexdigest(), 'Validated input changed: '+name
files=[ROOT/name for name in ['Main_Spire_Outer_Ring.blend','Main_Spire_Outer_Ring_Godot.glb','README.md','GODOT_IMPORT_VALIDATION.md']]
files += [ROOT/'previews'/name for name in ['01_outer_ring_spire_hero.png','02_outer_arm_entrance_cutaway.png','03_outer_arm_entrance_exterior.png']]
files += [ROOT/'scripts'/name for name in ['build_outer_ring.py','render_outer_ring.py','export_outer_ring.py','validate_outer_ring.py','package_outer_ring.py']]
files += [ROOT/'validation'/name for name in ['revision_manifest.json','geometry_validation.json','validation_summary.txt','route_samples.json','source_route_samples.json']]
for p in files:
    assert p.is_file() and p.stat().st_size>0,str(p)
    if p.suffix=='.png':assert p.stat().st_mtime>=(ROOT/'Main_Spire_Outer_Ring.blend').stat().st_mtime,'Stale preview: '+str(p)
manifest={str(p.relative_to(ROOT)):{'bytes':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in files}
(ROOT/'MANIFEST.json').write_text(json.dumps(manifest,indent=2)+'\n');files.append(ROOT/'MANIFEST.json')
archive=ROOT/'Ringworld_Main_Spire_4km_Outer_Ring.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for p in files:z.write(p,Path('Ringworld_Main_Spire_4km_Outer_Ring')/p.relative_to(ROOT))
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    assert len([n for n in z.namelist() if n.endswith('.blend')])==1
    assert len([n for n in z.namelist() if n.endswith('.glb')])==1
    assert len([n for n in z.namelist() if n.endswith('.png')])==3
print('PACKAGE_READY',archive,archive.stat().st_size,'bytes',len(files),'files')
