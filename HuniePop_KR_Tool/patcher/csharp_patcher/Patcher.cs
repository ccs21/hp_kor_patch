using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Patcher
{
    static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: Patcher.exe <HuniePop game folder>");
            Console.WriteLine("Example: Patcher.exe \"D:\\SteamLibrary\\steamapps\\common\\HuniePop\"");
            return 1;
        }

        string gameRoot = args[0].Trim('"');
        string managed = Path.Combine(gameRoot, "HuniePop_Data", "Managed");
        string asmPath = Path.Combine(managed, "Assembly-CSharp.dll");
        string hookPath = Path.Combine(managed, "KRHook.dll");

        if (!Directory.Exists(managed))
        {
            Console.WriteLine("[ERR] Managed folder not found: " + managed);
            return 1;
        }
        if (!File.Exists(asmPath))
        {
            Console.WriteLine("[ERR] Assembly-CSharp.dll not found: " + asmPath);
            return 1;
        }
        if (!File.Exists(hookPath))
        {
            Console.WriteLine("[ERR] KRHook.dll not found in Managed. Copy it here first:");
            Console.WriteLine("      " + hookPath);
            return 1;
        }

        // Backup once
        string backup = asmPath + ".bak";
        if (!File.Exists(backup))
        {
            File.Copy(asmPath, backup, overwrite: false);
            Console.WriteLine("[OK] Backup created: " + backup);
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managed);

        var rp = new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadWrite = false,
            InMemory = true
        };

        var asm = AssemblyDefinition.ReadAssembly(asmPath, rp);
        var mod = asm.MainModule;

        // Add KRHook assembly reference if missing
        if (!mod.AssemblyReferences.Any(r => r.Name == "KRHook"))
        {
            mod.AssemblyReferences.Add(new AssemblyNameReference("KRHook", new Version(1, 0, 0, 0)));
            Console.WriteLine("[OK] Added assembly reference: KRHook");
        }

        // Find LabelObject.SetText(string)
        var labelType = mod.GetType("LabelObject");
        if (labelType == null)
        {
            Console.WriteLine("[ERR] Type not found: LabelObject");
            return 1;
        }

        var setText = labelType.Methods.FirstOrDefault(m =>
            m.Name == "SetText" &&
            m.HasParameters &&
            m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.String"
        );

        if (setText == null)
        {
            Console.WriteLine("[ERR] Method not found: LabelObject.SetText(string)");
            return 1;
        }

        // Load KRHook.dll to import KRHook.OnSetText(object, string)
        var hookAsm = AssemblyDefinition.ReadAssembly(hookPath, new ReaderParameters { AssemblyResolver = resolver });
        var hookType = hookAsm.MainModule.GetType("KRHook");
        if (hookType == null)
        {
            Console.WriteLine("[ERR] KRHook type not found inside KRHook.dll");
            return 1;
        }

        var hookMethod = hookType.Methods.FirstOrDefault(m =>
            m.Name == "OnSetText" &&
            m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.FullName == "System.Object" &&
            m.Parameters[1].ParameterType.FullName == "System.String" &&
            m.ReturnType.FullName == "System.String"
        );

        if (hookMethod == null)
        {
            Console.WriteLine("[ERR] KRHook.OnSetText(object, string) not found or signature mismatch");
            return 1;
        }

        var hookImported = mod.ImportReference(hookMethod);

        // ---------------------------
        // SAFE PATCH:
        // Find callvirt tk2dTextMesh::set_text(string)
        // Replace the argument right before it (expect ldarg.1) with:
        //   ldarg.0
        //   ldarg.1
        //   call string KRHook::OnSetText(object, string)
        // ---------------------------
        var il = setText.Body.GetILProcessor();
        var ins = setText.Body.Instructions;

        bool patched = false;

        for (int i = 0; i < ins.Count; i++)
        {
            if (ins[i].OpCode == OpCodes.Callvirt && ins[i].Operand is MethodReference mr)
            {
                if (mr.Name == "set_text" &&
                    mr.Parameters.Count == 1 &&
                    mr.Parameters[0].ParameterType.FullName == "System.String")
                {
                    if (i > 0 && ins[i - 1].OpCode == OpCodes.Ldarg_1)
                    {
                        var target = ins[i];

                        // remove the original argument load
                        il.Remove(ins[i - 1]);

                        // push (this, originalText), call hook => string
                        il.InsertBefore(target, il.Create(OpCodes.Ldarg_0));
                        il.InsertBefore(target, il.Create(OpCodes.Ldarg_1));
                        il.InsertBefore(target, il.Create(OpCodes.Call, hookImported));

                        patched = true;
                        Console.WriteLine("[OK] Patched argument of tk2dTextMesh.set_text(string)");
                    }
                    break;
                }
            }
        }

        if (!patched)
        {
            Console.WriteLine("[ERR] Could not find tk2dTextMesh.set_text(string) call pattern in LabelObject.SetText.");
            return 1;
        }

        asm.Write(asmPath);
        Console.WriteLine("[SUCCESS] Patched Assembly-CSharp.dll");
        return 0;
    }
}
