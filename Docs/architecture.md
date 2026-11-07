# Architecture and validation boundary

The Windows app starts a hidden PowerShell process with literal argument lists.
For the supported example, Export calls the Python serializer. Validate and
Render call an existing Unity Editor through editor-only methods.

The independent serializer builds fresh scene and shared-asset object tables,
registers the supported HFF MonoScripts, remaps editor IDs to compact runtime
IDs, and writes a UnityFS streamed-scene bundle. Geometry placement and material
values come from the authored example. Basic cube, font and shader references
resolve to resources included in the installed game.

`schema-reference.data` was generated from this project's own scene. It supplies
binary type declarations, not a copied third-party level. The serializer strips
all reference object tables before writing new objects. The repository contains
no Unity/game executable, game managed DLL or original runtime test harness.

The native validation record is tied to the exact SHA256 of the example's game
data. Changing the authored scene invalidates that record. New output still gets
a structural reparse, but that alone is not a native-engine or gameplay test.

The recorded runtime test used isolated copies and suppressed game callbacks.
It checked native scene loading, registered script types, rigidbodies, joints,
200 physics steps and checkpoint ground support. It did not test human
completion, checkpoint gameplay callbacks, multiplayer, or the full game's UI.
