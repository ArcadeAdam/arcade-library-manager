import argparse,json,pathlib,xml.etree.ElementTree as E
root=pathlib.Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description='Build data-only recipes from audited catalog JSON, MAME XML, and a TeknoParrot profile folder. No ROMs are bundled.')
parser.add_argument('--audit-dir',required=True,type=pathlib.Path,help='Directory containing collection-game-notes.json, archive-game-catalog.json, missing-profile-archive-matches.json, mame-current.xml')
parser.add_argument('--teknoparrot-dir',required=True,type=pathlib.Path,help='TeknoParrot directory containing GameProfiles and Metadata')
args=parser.parse_args()
audit=args.audit_dir
tp=args.teknoparrot_dir

notes=json.loads((audit/'collection-game-notes.json').read_text(encoding='utf-8-sig'))
notes={n['profile_name'].removesuffix('.xml').lower():n for n in notes}
archives=json.loads((audit/'archive-game-catalog.json').read_text(encoding='utf-8-sig'))
byarchive={pathlib.PurePosixPath(a['Name']).stem.lower():a for a in archives}
# Audited exact-edition mappings missing from the original matching notes.
archive_overrides={'overrevb':('tp-roms_1','TeknoParrot Collection/Over Rev (Model 2B, Rev B) (1997)[Sega Model 2][TP].zip')}
byidentity={(a['Item'],a['Name']):a for a in archives}
missing={n['Id'].lower():n for n in json.loads((audit/'missing-profile-archive-matches.json').read_text(encoding='utf-8-sig'))}
recipes=[]
for f in (tp/'GameProfiles').glob('*.xml'):
 p=E.parse(f).getroot()
 if p.findtext('DevOnly','false')=='true':continue
 id=f.stem;n=notes.get(id.lower(),{});m=missing.get(id.lower(),{})
 archive=m.get('ArchiveFile') or byarchive.get(n.get('archive_name','').lower()) or {}
 if id.lower() in archive_overrides:
  archive=byidentity.get(archive_overrides[id.lower()])
  if not archive:raise ValueError('Audited archive is missing from catalog: '+str(archive_overrides[id.lower()]))
 exe=p.findtext('ExecutableName','')
 primary=m.get('ExpectedPath') or n.get('setup_exe','')
 secondary=m.get('ExpectedPath2') or n.get('setup_exe2','')
 if primary=='Dolphin.exe':primary=''
 romset=pathlib.Path(exe).stem if exe.lower().endswith('.zip') and ';' not in exe else ''
 if not primary and romset:primary=exe
 if not primary and ';' not in exe and '*' not in exe:primary=exe
 metadata=tp/'Metadata'/(id+'.json')
 md=json.loads(metadata.read_text(encoding='utf-8-sig')) if metadata.exists() else {}
 r=dict(Id=id,Name=md.get('game_name',id),PayloadId=id,ArchiveItem=archive.get('Item',''),ArchiveName=archive.get('Name',''),ArchiveSize=archive.get('Size',0),ArchiveMd5=archive.get('MD5',''),ArchiveSha1=archive.get('SHA1',''),PrimaryPath=primary,SecondaryPath=secondary,RomSet=romset,Roms=[],Disks=[],DependencySets=[],Notes=md.get('general_issues') or '')
 recipes.append(r)
# Shared payloads never produce redundant downloads.
payloads={}
for r in recipes:
 if r['ArchiveName']:
  key=(r['ArchiveItem'],r['ArchiveName']);r['PayloadId']=payloads.setdefault(key,r['Id'])
for r in recipes:
 if r['Id']=='DenshaDeGoRetro':r['PrimaryPath']=r'DGOREPR\TG4AC\Binaries\Win64\TG4AC-Win64-Shipping.exe';r['PayloadId']='DenshaDeGo'
 if r['Id']=='VF5FSapm3':r['PrimaryPath']='vfes.exe'
# This data-only recipe build is kept separate from runtime; no machine paths exported.
machines={}
for _,el in E.iterparse(audit/'mame-current.xml',events=('end',)):
 if el.tag=='machine':
  bios=el.findall('biosset');chosen=next((b.get('name') for b in bios if b.get('default')=='yes'),bios[0].get('name') if bios else None)
  roms=[dict(x.attrib) for x in el.findall('rom') if x.get('status')!='nodump' and x.get('optional')!='yes' and (not x.get('bios') or x.get('bios')==chosen)]
  machines[el.get('name')]=dict(roms=roms,disks=[dict(x.attrib) for x in el.findall('disk') if x.get('status')!='nodump' and x.get('optional')!='yes'],deps=[el.get('romof','')]+[x.get('name') for x in el.findall('device_ref')])
  el.clear()
for r in recipes:
 if not r['RomSet'] or r['RomSet'] not in machines:continue
 visited=set();required={};disks={};deps=[]
 def visit(name):
  if not name or name in visited or name not in machines:return
  visited.add(name);m=machines[name]
  if m['roms']:deps.append(name)
  for x in m['roms']:
   if x.get('crc') or x.get('sha1'):required[(x.get('crc'),x.get('size'))]=dict(Name=x['name'],Size=int(x.get('size','0')),Crc=x.get('crc',''),Sha1=x.get('sha1',''))
  for x in m['disks']:disks[x['name']]=dict(Name=x['name']+'.chd',Sha1=x.get('sha1',''))
  for d in m['deps']:visit(d)
 visit(r['RomSet'])
 # This profile launches the merged overrev.zip container but defaults to Model 2B Rev B.
 if r['Id']=='overrevb':visit('overrevb')
 r['Roms']=list(required.values());r['Disks']=list(disks.values());r['DependencySets']=deps
(root/'assets'/'recipes.json').write_text(json.dumps(recipes,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(profiles=len(recipes),archives=sum(bool(r['ArchiveName']) for r in recipes),rom_validators=sum(bool(r['Roms']) for r in recipes),disk_validators=sum(bool(r['Disks']) for r in recipes))))

