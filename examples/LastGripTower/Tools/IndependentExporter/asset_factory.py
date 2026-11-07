"""Original native Unity 2017 asset dictionaries for the Narae HFF map writer.

No mesh, material, texture, or compiled shader content is copied from a Workshop
map. The optional cube is generated mathematically. The default diffuse shader
reference resolves to an asset shipped with the user's installed HFF runtime.

`default` accepts either writer.default_schema (callable(class_id)->dict) or an
already allocated fresh schema dictionary. Each factory returns a fresh dict.
"""
from __future__ import annotations

import copy
import math
import struct
from collections.abc import Callable, Mapping, Sequence
from typing import Any


# Actual native path IDs observed in the installed official game's resource
# files, not YAML importer fileIDs. Shared-assets external indices are preserved
# by bundle_writer: 1 = resources/unity_builtin_extra, 2 = unity default resources.
BUILTIN_DIFFUSE_SHADER = {"m_FileID": 1, "m_PathID": 7}
BUILTIN_VERTEX_LIT_SHADER = {"m_FileID": 1, "m_PathID": 6}
BUILTIN_ARIAL_FONT = {"m_FileID": 2, "m_PathID": 10102}
BUILTIN_FONT_MATERIAL = {"m_FileID": 2, "m_PathID": 10100}
BUILTIN_CUBE_MESH = {"m_FileID": 2, "m_PathID": 10202}

SchemaInput = Callable[[int], dict[str, Any]] | Mapping[str, Any]


def _fresh(default: SchemaInput, class_id: int) -> dict[str, Any]:
    result = default(class_id) if callable(default) else default
    return copy.deepcopy(dict(result))


def _color(value: Sequence[float] | Mapping[str, float]) -> dict[str, float]:
    if isinstance(value, Mapping):
        result = {key: float(value.get(key, 1.0 if key == "a" else 0.0))
                  for key in ("r", "g", "b", "a")}
    else:
        if len(value) not in (3, 4):
            raise ValueError("color must contain RGB or RGBA")
        result = dict(zip(("r", "g", "b", "a"), map(float, (*value, 1.0) if len(value) == 3 else value)))
    if not all(math.isfinite(component) for component in result.values()):
        raise ValueError("color components must be finite")
    return result


def create_material(
    default: SchemaInput,
    name: str,
    color: Sequence[float] | Mapping[str, float],
    shader_ref: Mapping[str, int] | None = None,
    *,
    emission: float = 0.0,
    metallic: float = 0.0,
    smoothness: float = 0.35,
) -> dict[str, Any]:
    """Create an original opaque color material, compatible with Legacy/Diffuse.

    Legacy/Diffuse uses _Color and _MainTex. Extra emission/metallic/glossiness
    properties are carried for forward compatibility, but the verified builtin
    diffuse shader does not provide emissive or metallic rendering.
    """
    material = _fresh(default, 21)
    rgba = _color(color)
    shader = dict(shader_ref or BUILTIN_DIFFUSE_SHADER)
    if not all(key in shader for key in ("m_FileID", "m_PathID")):
        raise ValueError("shader_ref must be a native Unity PPtr")
    material.update({
        "m_Name": str(name),
        "m_Shader": shader,
        "m_ShaderKeywords": "",
        "m_LightmapFlags": 4,
        "m_EnableInstancingVariants": False,
        "m_DoubleSidedGI": False,
        "m_CustomRenderQueue": -1,
        "stringTagMap": [],
        "disabledShaderPasses": [],
        "m_SavedProperties": {
            "m_TexEnvs": [
                ("_MainTex", {
                    "m_Texture": {"m_FileID": 0, "m_PathID": 0},
                    "m_Scale": {"x": 1.0, "y": 1.0},
                    "m_Offset": {"x": 0.0, "y": 0.0},
                }),
            ],
            "m_Floats": [
                ("_Glossiness", float(smoothness)),
                ("_Metallic", float(metallic)),
                ("_Mode", 0.0),
                ("_SrcBlend", 1.0),
                ("_DstBlend", 0.0),
                ("_ZWrite", 1.0),
            ],
            "m_Colors": [
                ("_Color", rgba),
                ("_EmissionColor", {
                    "r": rgba["r"] * float(emission),
                    "g": rgba["g"] * float(emission),
                    "b": rgba["b"] * float(emission),
                    "a": 1.0,
                }),
            ],
        },
    })
    return material


def create_physic_material(
    default: SchemaInput,
    name: str = "Narae Glacier",
    dynamic_friction: float = 0.015,
    static_friction: float = 0.025,
    bounciness: float = 0.0,
    friction_combine: int = 1,
    bounce_combine: int = 1,
) -> dict[str, Any]:
    """Create original physical values; Unity2017 combines: 0 Average,
    1 Multiply, 2 Minimum, 3 Maximum. The default icy Multiply mode preserves
    the authored source material's very low contact friction.
    """
    values = (float(dynamic_friction), float(static_friction), float(bounciness))
    if not all(math.isfinite(value) and 0 <= value <= 1 for value in values):
        raise ValueError("friction and bounciness must be finite values in [0, 1]")
    if friction_combine not in range(4) or bounce_combine not in range(4):
        raise ValueError("Unity combine mode must be 0..3")
    material = _fresh(default, 134)
    material.update({
        "m_Name": str(name),
        "dynamicFriction": values[0],
        "staticFriction": values[1],
        "bounciness": values[2],
        "frictionCombine": int(friction_combine),
        "bounceCombine": int(bounce_combine),
    })
    return material


def create_mesh(default: SchemaInput, name: str = "Narae Original Unit Cube") -> dict[str, Any]:
    """Generate a unit cube: 24 original vertices/normals/UVs/tangents, 12 tris.

    Cube coordinates are +/-0.5. Each face has independent vertices so edge
    normals remain flat. Interleaved vertex layout is specific to Unity 2017:
    position channel0, normal1, UV0 channel3, tangent7; stride48 float32 bytes.
    """
    faces = (
        ((1, 0, 0), (0, 0, -1), (0, 1, 0)),
        ((-1, 0, 0), (0, 0, 1), (0, 1, 0)),
        ((0, 1, 0), (1, 0, 0), (0, 0, -1)),
        ((0, -1, 0), (1, 0, 0), (0, 0, 1)),
        ((0, 0, 1), (1, 0, 0), (0, 1, 0)),
        ((0, 0, -1), (-1, 0, 0), (0, 1, 0)),
    )
    uv_values = ((0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0))
    vertex_bytes = bytearray()
    indices: list[int] = []
    for face_index, (normal, tangent, vertical) in enumerate(faces):
        center = tuple(component * 0.5 for component in normal)
        for (u, v), (su, sv) in zip(uv_values, ((-.5, -.5), (.5, -.5), (.5, .5), (-.5, .5))):
            position = tuple(center[i] + tangent[i] * su + vertical[i] * sv for i in range(3))
            vertex_bytes.extend(struct.pack("<3f3f2f4f", *position, *normal, u, v, *tangent, 1.0))
        base = face_index * 4
        indices.extend(base + index for index in (0, 1, 2, 0, 2, 3))
    channels = [{"stream": 0, "offset": 0, "format": 0, "dimension": 0} for _ in range(8)]
    channels[0].update(offset=0, dimension=3)
    channels[1].update(offset=12, dimension=3)
    channels[3].update(offset=24, dimension=2)
    channels[7].update(offset=32, dimension=4)
    bounds = {"m_Center": {"x": 0.0, "y": 0.0, "z": 0.0}, "m_Extent": {"x": .5, "y": .5, "z": .5}}
    mesh = _fresh(default, 43)
    mesh.update({
        "m_Name": str(name),
        "m_SubMeshes": [{"firstByte": 0, "indexCount": 36, "topology": 0, "baseVertex": 0,
                          "firstVertex": 0, "vertexCount": 24, "localAABB": copy.deepcopy(bounds)}],
        "m_Shapes": {"vertices": [], "shapes": [], "channels": [], "fullWeights": []},
        "m_BindPose": [], "m_BoneNameHashes": [], "m_RootBoneNameHash": 0,
        "m_MeshCompression": 0, "m_IsReadable": True, "m_KeepVertices": True, "m_KeepIndices": True,
        "m_IndexFormat": 0, "m_IndexBuffer": list(struct.pack("<36H", *indices)), "m_Skin": [],
        "m_VertexData": {"m_CurrentChannels": 139, "m_VertexCount": 24, "m_Channels": channels, "m_DataSize": bytes(vertex_bytes)},
        "m_LocalAABB": bounds, "m_MeshUsageFlags": 0,
        "m_BakedConvexCollisionMesh": [], "m_BakedTriangleCollisionMesh": [],
    })
    compressed = mesh.setdefault("m_CompressedMesh", {})
    for key in ("m_Vertices", "m_UV", "m_Normals", "m_Tangents", "m_FloatColors"):
        compressed[key] = {"m_NumItems": 0, "m_Range": 0.0, "m_Start": 0.0, "m_Data": [], "m_BitSize": 0}
    for key in ("m_Weights", "m_NormalSigns", "m_TangentSigns", "m_BoneIndices", "m_Triangles"):
        compressed[key] = {"m_NumItems": 0, "m_Data": [], "m_BitSize": 0}
    compressed["m_UVInfo"] = 0
    return mesh
