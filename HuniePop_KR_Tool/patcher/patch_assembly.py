import sys
import os

# Mono.Cecil 필요
# pip install pythonnet
import clr

def die(msg):
    print("[ERROR]", msg)
    input("Press Enter...")
    sys.exit(1)

if len(sys.argv) < 2:
    die("Usage: patch_assembly.py <HuniePop game folder>")

game_root = sys.argv[1]
managed = os.path.join(game_root, "HuniePop_Data", "Managed")
asm_path = os.path.join(managed, "Assembly-CSharp.dll")
hook_path = os.path.join(managed, "KRHook.dll")

if not os.path.exists(asm_path):
    die("Assembly-CSharp.dll not found")

if not os.path.exists(hook_path):
    die("KRHook.dll not found (copy it first)")

# Cecil 로드
clr.AddReference("Mono.Cecil")
from Mono.Cecil import AssemblyDefinition
from Mono.Cecil.Cil import OpCodes

print("[*] Loading Assembly-CSharp.dll")
asm = AssemblyDefinition.ReadAssembly(asm_path)
mod = asm.MainModule

# KRHook 참조 추가
if not any(r.Name == "KRHook" for r in mod.AssemblyReferences):
    mod.AssemblyReferences.Add(mod.AssemblyReferences[0].__class__("KRHook", None))
    print("[OK] Added KRHook reference")

label = mod.GetType("LabelObject")
if label is None:
    die("LabelObject not found")

setText = None
for m in label.Methods:
    if m.Name == "SetText" and len(m.Parameters) == 1:
        setText = m
        break

if setText is None:
    die("LabelObject.SetText not found")

hookType = None
for t in mod.Types:
    if t.Name == "KRHook":
        hookType = t
        break

if hookType is None:
    die("KRHook type not found")

hookMethod = None
for m in hookType.Methods:
    if m.Name == "OnSetText":
        hookMethod = m
        break

if hookMethod is None:
    die("KRHook.OnSetText not found")

hookRef = mod.ImportReference(hookMethod)

il = setText.Body.GetILProcessor()
ins = setText.Body.Instructions

patched = False

for i in range(len(ins)):
    if ins[i].OpCode == OpCodes.Ldarg_1:
        # ldarg.1 -> ldarg.0, ldarg.1, call KRHook.OnSetText
        il.InsertAfter(ins[i], il.Create(OpCodes.Ldarg_0))
        il.InsertAfter(ins[i], il.Create(OpCodes.Ldarg_1))
        il.InsertAfter(ins[i], il.Create(OpCodes.Call, hookRef))
        il.Remove(ins[i])
        patched = True
        break

if not patched:
    die("Failed to patch IL")

backup = asm_path + ".bak"
if not os.path.exists(backup):
    os.rename(asm_path, backup)
    print("[OK] Backup created")

asm.Write(asm_path)
print("[SUCCESS] Assembly patched")
input("Done. Press Enter...")
