"""Original Unity 2017 HFF bundle serializer using type metadata as a schema.

Only serialization type trees, type hashes and MonoScript declarations are read
from the local reference bundle. No reference scene objects, meshes, materials,
textures, audio, strings or placements are kept in the output object tables.
All authored data must be supplied by the caller. This does not run Unity.
"""
from __future__ import annotations

from copy import copy, deepcopy
from pathlib import Path
import hashlib
import json
import sys
from typing import Any

_LIBS = Path(__file__).resolve().parents[1] / 'asset-research-libs'
if _LIBS.exists():
    sys.path.insert(0, str(_LIBS))
import UnityPy
from UnityPy.enums import ClassIDType
from UnityPy.files.ObjectReader import ObjectReader
from UnityPy.files.SerializedFile import LocalSerializedObjectIdentifier
from UnityPy.streams import EndianBinaryReader

_OWN_SCHEMA_REFERENCE=Path(__file__).with_name('schema-reference.data')
DEFAULT_TEMPLATE = _OWN_SCHEMA_REFERENCE
GAME_ASSEMBLIES = {
    'HumanAPI.Checkpoint': 'HumanAPI.dll',
    'HumanAPI.FallTrigger': 'HumanAPI.dll',
    'HumanAPI.Level': 'HumanAPI.dll',
    'HumanAPI.LevelPassTrigger': 'HumanAPI.dll',
}


def _zero(node):
    """Build values from schema; never use a reference object's field values."""
    kind = node.m_Type
    if kind == 'string': return ''
    if kind == 'TypelessData': return b''
    if kind == 'bool': return False
    if kind in ('float', 'double'): return 0.0
    if kind == 'pair': return [_zero(c) for c in node.m_Children]
    if node.m_Children and node.m_Children[0].m_Type == 'Array': return []
    if node.m_Children: return {c.m_Name: _zero(c) for c in node.m_Children}
    return 0


def _merge(node, supplied):
    """Complete partial author data according to runtime, not Editor YAML, fields."""
    if supplied is None: return _zero(node)
    kind = node.m_Type
    if kind in ('string', 'TypelessData', 'bool', 'float', 'double') or not node.m_Children:
        return supplied
    if kind == 'pair':
        return [_merge(n, supplied[i]) for i,n in enumerate(node.m_Children)]
    if node.m_Children[0].m_Type == 'Array':
        child = node.m_Children[0].m_Children[1]
        return [_merge(child, item) for item in supplied]
    if not isinstance(supplied, dict):
        raise TypeError(f'{node.m_Name} requires a dict, got {type(supplied).__name__}')
    return {child.m_Name: _merge(child, supplied.get(child.m_Name)) for child in node.m_Children}


class HffBundleWriter:
    """Author two fresh serialized files and a streamed-scene UnityFS bundle.

    References in scene data: file 0 = scene, 1 = this bundle's sharedAssets,
    2 = library/unity default resources.
    References in shared data: file 0 = sharedAssets,
    1 = resources/unity_builtin_extra, 2 = library/unity default resources.
    MonoBehaviour m_Script is set automatically from script_name during add.
    """
    def __init__(self, template_path=DEFAULT_TEMPLATE, scene_name='LastGripTower',
                 scene_asset_path='Assets/LastGrip/LastGripTower.unity'):
        self.template_path = Path(template_path)
        self.scene_name = scene_name
        self.scene_asset_path = scene_asset_path
        self._environment = UnityPy.load(str(self.template_path))
        reference_bundle = next(iter(self._environment.files.values()))
        reference_shared = next(f for n,f in reference_bundle.files.items()
                                if n.endswith('.sharedAssets') and hasattr(f,'objects'))
        reference_scene = next(f for n,f in reference_bundle.files.items()
                               if hasattr(f,'objects') and not n.endswith('.sharedAssets'))
        if reference_scene.unity_version != '2017.4.13f1' or reference_scene.header.version != 17:
            raise ValueError('Only the verified Unity 2017.4.13f1 serialized format 17 is supported.')
        self._native_types = {}
        self._native_sources = {}
        for file in (reference_scene, reference_shared):
            for typ in file.types:
                if typ.class_id != 114 and typ.node is not None:
                    self._native_types.setdefault(typ.class_id, typ)
                    self._native_sources.setdefault(typ.class_id, str(self.template_path))
        # A second installed matching-format map supplies the native TextMesh
        # serialization schema missing from the primary reference. Only type
        # metadata is retained; none of its objects or strings are copied.
        text_schema_source = self.template_path.parent.parent / '2176972256' / 'data'
        if 102 not in self._native_types and text_schema_source.is_file():
            extra_environment = UnityPy.load(str(text_schema_source))
            extra_bundle = next(iter(extra_environment.files.values()))
            for file in extra_bundle.files.values():
                if not hasattr(file,'types') or file.unity_version != '2017.4.13f1': continue
                for typ in file.types:
                    if typ.class_id == 102 and typ.node is not None:
                        self._native_types[102] = typ
                        self._native_sources[102] = str(text_schema_source)
                        break
        # MonoScript field declarations and property hashes are schema metadata,
        # not executable script logic or any map object's field data.
        script_decl = {}
        for obj in reference_shared.objects.values():
            if obj.class_id == 115:
                decl = obj.read_typetree(check_read=False)
                fullname = '.'.join(p for p in (decl['m_Namespace'],decl['m_ClassName']) if p)
                script_decl[obj.path_id] = (fullname, decl)
        self._script_types = {}
        self._script_decl = {}
        for file in (reference_scene, reference_shared):
            for typ in file.types:
                if typ.class_id != 114 or typ.script_type_index < 0: continue
                identity = file.script_types[typ.script_type_index]
                # Both reference files declare these in sharedAssets. The scene
                # uses external index 1; sharedAssets uses its local index 0.
                item = script_decl.get(identity.local_identifier_in_file)
                if not item: continue
                fullname, decl = item
                self._script_types.setdefault(fullname, typ)
                self._script_decl.setdefault(fullname, decl)
        self.bundle = copy(reference_bundle)
        self.bundle.files = {}
        self.bundle.name = 'data'
        self.scene_file_name = 'BuildPlayer-' + scene_name
        self.shared_file_name = self.scene_file_name + '.sharedAssets'
        self.shared = self._empty_file(reference_shared, self.shared_file_name)
        self.scene = self._empty_file(reference_scene, self.scene_file_name)
        self.shared.externals = [copy(x) for x in reference_shared.externals]
        self.scene.externals = [copy(x) for x in reference_scene.externals]
        self.scene.externals[0].path = f'archive:/{self.scene_file_name.lower()}/{self.shared_file_name.lower()}'
        self.bundle.files = {self.shared_file_name:self.shared, self.scene_file_name:self.scene}
        self._type_indices = {id(self.scene):{}, id(self.shared):{}}
        self._script_paths = {}
        self._script_indices = {id(self.scene):{}, id(self.shared):{}}
        self._next_script_path = 900_000_000
        self._authored_scene_ids = set()
        self._authored_shared_ids = set()
        self._last_save = None
        self._add_manifests()

    def _empty_file(self, reference, name):
        file = copy(reference)
        file.header = copy(reference.header)
        file.parent = self.bundle
        file.name = name
        file.types = []
        file.objects = {}
        file.script_types = []
        file.externals = []
        file.ref_types = [] if reference.header.version >= 20 else None
        file.userInformation = ''
        file.assetbundle = None
        file._cache = {}
        file.is_changed = True
        # No raw reference object is ever read from this empty reader.
        file.reader = EndianBinaryReader(b'', endian=reference.header.endian)
        return file

    def available_script_names(self):
        return sorted(self._script_types)

    def _script_name(self, name):
        if name in self._script_types: return name
        matches = [key for key in self._script_types if key.rsplit('.',1)[-1] == name]
        if len(matches)==1: return matches[0]
        raise KeyError(f'No unique reference schema for script {name!r}. Available: {self.available_script_names()}')

    def _schema_type(self, class_id, script_name=None):
        if class_id == 114:
            if not script_name: raise ValueError('MonoBehaviour class 114 requires script_name.')
            return self._script_types[self._script_name(script_name)]
        try: return self._native_types[class_id]
        except KeyError: raise KeyError(f'No serialization schema for native class {class_id}')

    def default_schema(self, class_id:int, script_name:str|None=None)->dict:
        return _zero(self._schema_type(class_id,script_name).node)

    def _register_script(self, file, name):
        name = self._script_name(name)
        if name not in self._script_paths:
            pid = self._next_script_path
            self._next_script_path += 1
            decl = self._script_decl[name]
            data = self.default_schema(115)
            data.update(m_Name=decl['m_ClassName'], m_ClassName=decl['m_ClassName'],
                        m_Namespace=decl['m_Namespace'],
                        m_AssemblyName=GAME_ASSEMBLIES.get(name,decl['m_AssemblyName']),
                        m_ExecutionOrder=decl['m_ExecutionOrder'],
                        m_PropertiesHash=deepcopy(decl['m_PropertiesHash']),m_IsEditorScript=False)
            self._add(self.shared,115,pid,data,None,authored=False)
            self._script_paths[name] = pid
        indices = self._script_indices[id(file)]
        if name not in indices:
            entry = object.__new__(LocalSerializedObjectIdentifier)
            entry.local_serialized_file_index = 1 if file is self.scene else 0
            entry.local_identifier_in_file = self._script_paths[name]
            indices[name] = len(file.script_types)
            file.script_types.append(entry)
        return indices[name], self._script_paths[name]

    def _type_index(self, file, class_id, script_name):
        name = self._script_name(script_name) if class_id==114 else None
        key = (class_id,name)
        indices = self._type_indices[id(file)]
        if key not in indices:
            typ = copy(self._schema_type(class_id,name))
            if class_id==114:
                typ.script_type_index = self._register_script(file,name)[0]
            else: typ.script_type_index = -1
            indices[key] = len(file.types)
            file.types.append(typ)
        return indices[key]

    def _add(self,file,class_id,path_id,data,script_name,authored=True):
        if not isinstance(path_id,int) or path_id<=0: raise ValueError('path_id must be a positive int')
        if path_id in file.objects: raise ValueError(f'Duplicate path ID {path_id} in {file.name}')
        type_id = self._type_index(file,class_id,script_name)
        typ = file.types[type_id]
        data = _merge(typ.node,data)
        if class_id==114:
            _,script_path = self._register_script(file,script_name)
            data['m_Script'] = {'m_FileID':1 if file is self.scene else 0,'m_PathID':script_path}
        obj = ObjectReader(file,EndianBinaryReader(b'',endian=file.header.endian),
                           path_id,type_id,typ,class_id,ClassIDType(class_id),0,0,None,None)
        obj.save_typetree(data)
        file.objects[path_id] = obj
        if authored:
            (self._authored_scene_ids if file is self.scene else self._authored_shared_ids).add(path_id)
        return obj

    def add_scene_object(self,class_id:int,path_id:int,data:dict,script_name=None):
        return self._add(self.scene,class_id,path_id,data,script_name)

    def add_shared_object(self,class_id:int,path_id:int,data:dict,script_name=None):
        return self._add(self.shared,class_id,path_id,data,script_name)

    def _add_manifests(self):
        # Streamed scenes identify their scene by the container key; they have
        # no ordinary root asset PPtr. Names and all object tables are new.
        pre = self.default_schema(150)
        pre.update(m_Name='',m_Assets=[],m_Dependencies=[])
        self._add(self.shared,150,1,pre,None,authored=False)
        manifest = self.default_schema(142)
        zero_asset = {'preloadIndex':0,'preloadSize':0,'asset':{'m_FileID':0,'m_PathID':0}}
        manifest.update(m_Name='data',m_PreloadTable=[],
                        m_Container=[[self.scene_asset_path,deepcopy(zero_asset)]],
                        m_MainAsset=deepcopy(zero_asset),m_RuntimeCompatibility=1,
                        m_AssetBundleName='',m_Dependencies=[],m_IsStreamedSceneAssetBundle=True,
                        m_ExplicitDataLayout=0,m_PathFlags=3,m_SceneHashes=[])
        self._add(self.shared,142,2,manifest,None,authored=False)

    def save_bundle(self,output_path,packer='lz4'):
        path = Path(output_path)
        path.parent.mkdir(parents=True,exist_ok=True)
        if path.resolve()==self.template_path.resolve():
            raise ValueError('Refusing to overwrite the reference bundle')
        raw = self.bundle.save(packer=packer)
        path.write_bytes(raw)
        # Reparse exact produced bytes to validate every authored object's type
        # and byte length, as well as the new streamed-scene manifest.
        reopened = next(iter(UnityPy.load(raw).files.values()))
        for file in reopened.files.values():
            if hasattr(file,'objects'):
                for obj in file.objects.values(): obj.read_typetree(check_read=True)
        self._last_save = {'path':str(path.resolve()),'bytes':len(raw),'sha256':hashlib.sha256(raw).hexdigest(),
                           'scene_objects':len(self.scene.objects),'shared_objects':len(self.shared.objects),
                           'authored_scene_objects':len(self._authored_scene_ids),
                           'authored_shared_objects':len(self._authored_shared_ids),
                           'script_registrations':dict(self._script_paths),'structural_reparse':'passed',
                           'gameplay_verified':False}
        return deepcopy(self._last_save)

    def schema_manifest(self):
        return {'schema_source':str(self.template_path),'unity_version':self.scene.unity_version,
                'format':self.scene.header.version,'scene_name':self.scene_name,
                'native_classes':{str(k):{'type':v.node.m_Type,'fields':[c.m_Name for c in v.node.m_Children],
                                          'schema_source':self._native_sources[k]} for k,v in self._native_types.items()},
                'scripts':{name:{'assembly':GAME_ASSEMBLIES.get(name,decl['m_AssemblyName']),
                                 'execution_order':decl['m_ExecutionOrder'],
                                 'fields':[c.m_Name for c in self._script_types[name].node.m_Children]}
                           for name,decl in self._script_decl.items()},
                'original_scene_objects_copied':0,'original_art_assets_copied':0,
                'scope':'Serialization schemas, type hashes and class identity metadata only; no original map object data or design.'}


_DEFAULT_WRITER = None
def default_schema(class_id:int,script_name:str|None=None)->dict:
    global _DEFAULT_WRITER
    if _DEFAULT_WRITER is None: _DEFAULT_WRITER = HffBundleWriter()
    return _DEFAULT_WRITER.default_schema(class_id,script_name)


if __name__=='__main__':
    writer = HffBundleWriter()
    manifest = Path(__file__).with_name('schema_manifest.json')
    manifest.write_text(json.dumps(writer.schema_manifest(),ensure_ascii=False,indent=2),encoding='utf-8')
    # Original smoke scene: a named empty root and its identity transform.
    go = writer.default_schema(1)
    go.update(m_Name='Narae_Original_Smoke_Root',m_Component=[{'component':{'m_FileID':0,'m_PathID':101}}],m_IsActive=True)
    tr = writer.default_schema(4)
    tr.update(m_GameObject={'m_FileID':0,'m_PathID':100},m_LocalRotation={'x':0.0,'y':0.0,'z':0.0,'w':1.0},
              m_LocalScale={'x':1.0,'y':1.0,'z':1.0})
    writer.add_scene_object(1,100,go)
    writer.add_scene_object(4,101,tr)
    mb = writer.default_schema(114,'HumanAPI.Checkpoint')
    mb.update(m_GameObject={'m_FileID':0,'m_PathID':100},m_Enabled=True,number=0)
    writer.add_scene_object(114,102,mb,'HumanAPI.Checkpoint')
    print(json.dumps(writer.save_bundle(Path(__file__).with_name('writer_smoke.data')),indent=2))
