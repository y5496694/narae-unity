"""Write the authored LAST GRIP map using original native scene data.

No Unity Editor, license alteration or reference-map object data is used.
Schema metadata supplies the binary format; geometry is our authored scene.
"""
from pathlib import Path
import copy
import hashlib
import json
import math
import re
import shutil
import sys

SCRIPT_DIR=Path(__file__).resolve().parent
WORKSPACE=next((p for p in SCRIPT_DIR.parents if (p/'work/pythonlibs').is_dir()),SCRIPT_DIR.parents[1])
for lib in ('pythonlibs','asset-research-libs'):
    candidate=WORKSPACE/'work'/lib
    if candidate.is_dir():sys.path.insert(0,str(candidate))
import yaml
from Crypto.Hash import MD4
from bundle_writer import HffBundleWriter
from asset_factory import create_material, create_physic_material

PROJECT=next((p for p in SCRIPT_DIR.parents if (p/'Assets/LastGrip/LastGripTower.unity').is_file()),WORKSPACE/'outputs/LastGripTower')
OUTPUT=PROJECT/'Build/LastGrip'
DLL_GUID='c386120fc5e1d664eaf46f801f628209'
SCRIPT_NAMES=['HumanAPI.BuiltinLevel','HumanAPI.Checkpoint','HumanAPI.FallTrigger','HumanAPI.LevelPassTrigger','HumanAPI.NodeGraph',
              'HumanAPI.SoundLibrary','HumanAPI.SoundManager','Multiplayer.NetBody','Multiplayer.NetIdentity','Multiplayer.NetScene',
              'Multiplayer.NetSceneManager','SignalSine','SignalTweenPosition']


def script_id(name):
    ns,_,cls=name.rpartition('.')
    return int.from_bytes(MD4.new(('s\0\0\0'+ns+cls).encode()).digest()[:4],'little',signed=True)


SCRIPTS={script_id(name):name for name in SCRIPT_NAMES}


def parse_unity(path):
    parts=re.split(r'^--- !u!(\d+) &(\d+)\s*$',path.read_text(encoding='utf-8-sig'),flags=re.M)[1:]
    result={}
    for i in range(0,len(parts),3):
        data=yaml.safe_load(parts[i+2])
        result[int(parts[i+1])]=(int(parts[i]),next(iter(data.values())))
    return result


def ptr(path=0,file=0):return {'m_FileID':file,'m_PathID':path}
def yref(path=0):return {'fileID':path}
def vec(x=0,y=0,z=0):return dict(x=x,y=y,z=z)


def convert_motions(docs):
    """Use only stock joints: orbiting pads and articulated Ferris lifts.

    The legacy SignalSine/Tween components are replaced by native physics
    because their binary script schemas are absent from the local references.
    """
    nextid=50_000_000
    def new(cls,data):
        nonlocal nextid
        nextid+=1;docs[nextid]=(cls,data);return nextid
    def component(go,cls,data):
        data={'m_GameObject':yref(go),**data}
        pid=new(cls,data);docs[go][1]['m_Component'].append({'component':yref(pid)});return pid
    def mono(go,name,**kw):return component(go,114,dict(m_Enabled=1,m_Script={'fileID':script_id(name),'guid':DLL_GUID,'type':3},**kw))
    def hinge(go,connected=0,axis=(0,1,0),anchor=(0,0,0),connected_anchor=(0,0,0),motor=0):
        return component(go,59,dict(m_ConnectedBody=yref(connected),m_Anchor=vec(*anchor),m_Axis=vec(*axis),
            m_AutoConfigureConnectedAnchor=0,m_ConnectedAnchor=vec(*connected_anchor),m_UseSpring=0,
            m_Spring=dict(spring=0,damper=0,targetPosition=0),m_UseMotor=int(motor!=0),
            m_Motor=dict(targetVelocity=motor,force=18000,freeSpin=0),m_UseLimits=0,
            m_Limits=dict(min=0,max=0,bounciness=0,bounceMinVelocity=.2,contactDistance=1),
            m_BreakForce=1000000,m_BreakTorque=1000000,m_EnableCollision=0,m_EnablePreprocessing=1))
    moving=[]
    for fid,(cls,data) in list(docs.items()):
        if cls==114 and SCRIPTS.get(data['m_Script']['fileID'])=='SignalTweenPosition':moving.append(data['m_GameObject']['fileID'])
    removed=[]
    for fid,(cls,data) in list(docs.items()):
        if cls==114 and SCRIPTS.get(data['m_Script']['fileID']) in ('SignalSine','SignalTweenPosition'):
            go=data['m_GameObject']['fileID'];docs[go][1]['m_Component']=[c for c in docs[go][1]['m_Component'] if c['component']['fileID']!=fid]
            removed.append(fid);del docs[fid]
    netid=100
    for go in moving:
        obj=docs[go][1]
        comps=[(c['component']['fileID'],docs[c['component']['fileID']]) for c in obj['m_Component']]
        tid,tr=next((pid,d) for pid,(cls,d) in comps if cls==4)
        rigid,rb=next((pid,d) for pid,(cls,d) in comps if cls==54)
        rb.update(m_UseGravity=1,m_IsKinematic=0)
        for pid,(cls,data) in comps:
            if cls==114 and SCRIPTS.get(data['m_Script']['fileID'])=='Multiplayer.NetBody':data['isKinematic']=0
        pos=tr['m_LocalPosition'];q=tr['m_LocalRotation'];parent=tr['m_Father']['fileID']
        if obj['m_Name']=='Elevator':
            # A driven axle carries a freely hanging deck. No custom runtime
            # code: both the drive and the upright deck use native HingeJoint.
            carrier=new(1,dict(m_Name='Narae_Ferris_Lift_Axle',m_Layer=0,m_TagString='Untagged',m_IsActive=1,m_Component=[]))
            carrier_tr=component(carrier,4,dict(m_LocalRotation=copy.deepcopy(q),m_LocalPosition=vec(pos['x'],pos['y']+1.05,pos['z']),
                m_LocalScale=vec(1,1,1),m_Children=[],m_Father=yref(parent)))
            docs[parent][1]['m_Children'].append(yref(carrier_tr))
            carrier_rb=component(carrier,54,dict(m_Mass=70,m_Drag=.05,m_AngularDrag=.1,m_UseGravity=0,m_IsKinematic=0,m_Interpolate=1,m_Constraints=0,m_CollisionDetection=1))
            component(carrier,65,dict(m_Material=yref(),m_IsTrigger=0,m_Enabled=1,m_Size=vec(.15,.15,.15),m_Center=vec()))
            mono(carrier,'Multiplayer.NetIdentity',sceneId=netid);netid+=1
            mono(carrier,'Multiplayer.NetBody',syncPosition=2,syncRotation=2,smooth=1,respawn=0,isKinematic=0)
            hinge(carrier,axis=(1,0,0),connected_anchor=(pos['x'],pos['y']+1.05,pos['z']),motor=27)
            hinge(go,connected=carrier_rb,axis=(1,0,0),anchor=(0,.25,0),connected_anchor=(0,-.8,0))
        else:
            # Local +X is the route's lateral axis. Rotation keeps the deck
            # horizontal while its centre travels around the offset axle.
            qy,qw=q['y'],q['w'];cosy=1-2*qy*qy;siny=2*qy*qw
            hinge(go,anchor=(1.2,0,0),connected_anchor=(pos['x']+1.2*cosy,pos['y'],pos['z']-1.2*siny),motor=25)
    return {'legacy_components_removed':len(removed),'native_moving_decks':len(moving),'lift_carriers_added':5,
            'changes':['횡이동 발판은 수평 궤도 운동으로 구성','승강 발판은 자유 힌지로 매달린 회전 승강 플랫폼으로 구성']}


def build(output=OUTPUT,omit=()):
    output=Path(output);output.mkdir(parents=True,exist_ok=True)
    writer=HffBundleWriter()
    docs=parse_unity(PROJECT/'Assets/LastGrip/LastGripTower.unity')
    changes=convert_motions(docs)
    omitted_ids={fid for fid,(cls,data) in docs.items() if cls in omit}
    if 102 in omit:
        text_gos={d['m_GameObject']['fileID'] for cls,d in docs.values() if cls==102}
        omitted_ids.update(fid for fid,(cls,data) in docs.items() if cls==23 and data['m_GameObject']['fileID'] in text_gos)
    for fid in omitted_ids:del docs[fid]
    for fid,(cls,data) in docs.items():
        if cls==1:data['m_Component']=[x for x in data['m_Component'] if x['component']['fileID'] not in omitted_ids]
    # Editor prefab IDs can be >2^50. The native2017 scene loader crashes on
    # those in a streamed scene even though the file format stores int64 IDs.
    # Compile fresh compact scene identities and remap every local pointer.
    scene_ids={fid:index+100 for index,fid in enumerate(docs)}
    assets={};pid=10000
    for material_path in sorted((PROJECT/'Assets/LastGrip/Materials').glob('*.mat')):
        original=next(iter(parse_unity(material_path).values()))[1]
        rgba=next(x['_Color'] for x in original['m_SavedProperties']['m_Colors'] if '_Color' in x)
        meta=Path(str(material_path)+'.meta').read_text(encoding='utf-8-sig')
        guid=re.search(r'^guid: (\w+)',meta,re.M).group(1)
        pid+=1;assets[guid]=pid
        material=create_material(writer.default_schema,original['m_Name'],rgba)
        writer.add_shared_object(21,pid,material)
    ice_path=PROJECT/'Assets/LastGrip/Glacier.physicMaterial'
    ice_guid=re.search(r'^guid: (\w+)',Path(str(ice_path)+'.meta').read_text(),re.M).group(1)
    pid+=1;assets[ice_guid]=pid
    writer.add_shared_object(134,pid,create_physic_material(writer.default_schema))
    dropped_asset_refs=[]
    def convert(value,field=''):
        if isinstance(value,dict):
            if 'fileID' in value:
                fid=value['fileID'];guid=value.get('guid')
                if not fid:return ptr()
                if not guid:return ptr() if fid in omitted_ids else ptr(scene_ids.get(fid,fid))
                if guid in assets:return ptr(assets[guid],1)
                if guid.startswith('0000000000000000'):
                    if fid==10202:return ptr(10202,2)
                    if fid==12800000:return ptr(10102,2)
                    if fid==10302:return ptr(10100,2)
                    # Default skybox and old editor-only cookie references are
                    # intentionally omitted; lighting is explicitly authored.
                    return ptr()
                dropped_asset_refs.append(dict(field=field,guid=guid,fileID=fid));return ptr()
            return {k:convert(v,field+'.'+k) for k,v in value.items()}
        if isinstance(value,list):return [convert(v,field) for v in value]
        return value
    runtime_defaults={}
    defaults_path=Path(__file__).with_name('runtime_defaults.json')
    if defaults_path.exists():
        runtime_defaults=json.loads(defaults_path.read_text(encoding='utf-8-sig'))
        runtime_defaults=runtime_defaults.get('defaults',runtime_defaults)
    counts={};scripts={}
    tags={'Untagged':0,'Respawn':1,'Finish':2,'EditorOnly':3,'MainCamera':4,'Player':5,'GameController':6,'Level':20004}
    for fid,(cls,authored) in docs.items():
        script=None
        data=copy.deepcopy(authored)
        if cls==114:
            script=SCRIPTS[data['m_Script']['fileID']]
            del data['m_Script']
            scripts[script]=scripts.get(script,0)+1
        data=convert(data)
        if cls==1:data['m_Tag']=tags.get(authored.get('m_TagString','Untagged'),0)
        if cls==23:
            data.update(m_LightmapIndex=65535,m_LightmapIndexDynamic=65535,
                m_LightmapTilingOffset=dict(x=1,y=1,z=0,w=0),m_LightmapTilingOffsetDynamic=dict(x=1,y=1,z=0,w=0))
        if cls==59:
            data.setdefault('m_MassScale',1.0)
            data.setdefault('m_ConnectedMassScale',1.0)
        if cls==102:
            c=authored.get('m_Color',dict(r=1,g=1,b=1,a=1))
            packed=sum(round(max(0,min(1,c[k]))*255)<<(8*i) for i,k in enumerate(('r','g','b','a')))
            data['m_Color']={'rgba':packed}
        if cls==104:
            data.update(m_AmbientMode=3,m_AmbientIntensity=1,m_AmbientSkyColor=dict(r=.52,g=.62,b=.78,a=1),m_SkyboxMaterial=ptr())
        if script:
            default=copy.deepcopy(runtime_defaults.get(script,{}))
            if script=='HumanAPI.BuiltinLevel':
                default={**runtime_defaults.get('HumanAPI.Level',{}),**default}
                # A zero velocity cap prevents locomotion; retain an explicit
                # generous cap even when older author YAML omitted this field.
                default.setdefault('MaxHumanVelocity',150.0)
                default.setdefault('HasWeather',False)
            data={**default,**data}
            if script=='HumanAPI.BuiltinLevel':
                source_hash=hashlib.sha256((PROJECT/'Assets/LastGrip/LastGripTower.unity').read_bytes()).digest()
                data['netHash']=int.from_bytes(source_hash[:4],'little')|0x80000000
        writer.add_scene_object(cls,scene_ids[fid],data,script_name=script)
        counts[str(cls)]=counts.get(str(cls),0)+1
    for cls,fid in ((29,3),(157,5),(196,6)):
        if cls in omit:continue
        try: default=writer.default_schema(cls)
        except KeyError:continue
        writer.add_scene_object(cls,fid,default)
    stats=writer.save_bundle(output/'data')
    # Use the game's own metadata schema declarations from the official SDK.
    ownhash=int.from_bytes(hashlib.sha256((output/'data').read_bytes()).digest()[:4],'little')
    metadata=dict(type='Levels',workshopId=0,title='LAST GRIP',description='12-stage, 72m physics tower. Checkpoints every three stages. Original Narae scene. Native joint-driven orbiting and Ferris lift platforms. Initial independent-export build: gameplay balancing remains unverified.',
        typeTags=1,playerTags=1,themeTags='Castle,PowerPlant',flags=1,hash=ownhash,levelType=0)
    (output/'metadata.json').write_text(json.dumps(metadata,ensure_ascii=False),encoding='utf-8')
    shutil.copyfile(PROJECT/'Previews/overview.png',output/'thumbnail.png')
    report=dict(status='serialized',format='UnityFS streamed scene / Unity2017.4.13f1',editor_used=False,license_modified=False,
        scene='Assets/LastGrip/LastGripTower.unity',counts=counts,scripts=scripts,movement_changes=changes,
        native_writer=stats,dropped_optional_asset_refs=dropped_asset_refs,compact_scene_ids=True,gameplay_verified=False,
        data_sha256=hashlib.file_digest((output/'data').open('rb'),'sha256').hexdigest())
    certificate=SCRIPT_DIR/'validation-native.json'
    report['native_runtime_validation']='not_run_for_this_hash'
    if certificate.is_file():
        validation=json.loads(certificate.read_text(encoding='utf-8-sig'))
        if validation.get('status')=='passed' and validation.get('sha256')==report['data_sha256']:
            report['native_runtime_validation']='passed'
            report['native_validation_scope']='Isolated Unity2017 runtime, game callbacks suppressed; native loading, scene, scripts, 200 physics steps and checkpoint ground support. No playthrough or multiplayer test.'
            shutil.copyfile(certificate,output/'validation-native.json')
    (output/'export-report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(report,ensure_ascii=False,indent=2))
    return report


if __name__=='__main__':
    import argparse
    ap=argparse.ArgumentParser();ap.add_argument('output',nargs='?',default=str(OUTPUT));ap.add_argument('--omit',default='')
    args=ap.parse_args()
    build(Path(args.output),tuple(int(x) for x in args.omit.split(',') if x))
