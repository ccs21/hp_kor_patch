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
            Console.WriteLine("[ERR] KRHook.dll not found in Managed: " + hookPath);
            return 1;
        }

        // Backup once
        string backup = asmPath + ".bak";
        if (!File.Exists(backup))
        {
            File.Copy(asmPath, backup, overwrite: false);
            Console.WriteLine("[OK] Backup created: " + backup);
        }
        else
        {
            Console.WriteLine("[OK] Backup exists: " + backup);
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

        // Ensure KRHook reference exists
        if (!mod.AssemblyReferences.Any(r => r.Name == "KRHook"))
            mod.AssemblyReferences.Add(new AssemblyNameReference("KRHook", new Version(1, 0, 0, 0)));

        // Import KRHook.OnSetText(object,string)
        var hookAsm = AssemblyDefinition.ReadAssembly(hookPath, new ReaderParameters { AssemblyResolver = resolver });
        var hookType = hookAsm.MainModule.GetType("KRHook");
        if (hookType == null)
        {
            Console.WriteLine("[ERR] Type KRHook not found inside KRHook.dll");
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
            Console.WriteLine("[ERR] KRHook.OnSetText(object,string) not found or signature mismatch");
            return 1;
        }

        var hookImported = mod.ImportReference(hookMethod);

        int patched = 0;

        // 1) Inject translation hook into LabelObject.SetText(string)
        patched += PatchMethodStart(mod, "LabelObject", "SetText", hookImported);

        // 2) Disable typewriter effect by forcing dialogReadPercent=1 before _dialogSequence.Play()
        patched += PatchDisableTypewriter(mod);

        asm.Write(asmPath);

        Console.WriteLine("[SUCCESS] Patched Assembly-CSharp.dll. patched_items=" + patched);
        return 0;
    }

    // Patch: at method start, rewrite arg1 string:
    // arg1 = KRHook.OnSetText(this, arg1)
    static int PatchMethodStart(ModuleDefinition mod, string typeName, string methodName, MethodReference hookImported)
    {
        var t = mod.GetType(typeName);
        if (t == null)
        {
            Console.WriteLine("[WARN] Type not found: " + typeName);
            return 0;
        }

        var m = t.Methods.FirstOrDefault(x =>
            x.Name == methodName &&
            x.HasBody &&
            x.Parameters.Count == 1 &&
            x.Parameters[0].ParameterType.FullName == "System.String"
        );

        if (m == null)
        {
            Console.WriteLine("[WARN] Method not found: " + typeName + "." + methodName + "(string)");
            return 0;
        }

        if (AlreadyPatched(m))
        {
            Console.WriteLine("[OK] Already patched: " + typeName + "." + methodName);
            return 0;
        }

        var il = m.Body.GetILProcessor();
        var first = m.Body.Instructions.FirstOrDefault(i => i.OpCode != OpCodes.Nop) ?? m.Body.Instructions.First();

        il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_1));
        il.InsertBefore(first, il.Create(OpCodes.Call, hookImported));
        il.InsertBefore(first, il.Create(OpCodes.Starg_S, m.Parameters[0]));

        Console.WriteLine("[OK] Patched: " + typeName + "." + methodName);
        return 1;
    }

    // Disable typewriter:
    // In Girl.ReadDialogLine(...), before Sequence.Play():
    // this.dialogReadPercent = 1f;
    static int PatchDisableTypewriter(ModuleDefinition mod)
    {
        var girl = mod.GetType("Girl");
        if (girl == null)
        {
            Console.WriteLine("[WARN] Girl type not found");
            return 0;
        }

        var m = girl.Methods.FirstOrDefault(x =>
            x.Name == "ReadDialogLine" &&
            x.HasBody &&
            x.Parameters.Count >= 1 &&
            x.Parameters[0].ParameterType.Name == "DialogLine"
        );

        if (m == null)
        {
            Console.WriteLine("[WARN] Girl.ReadDialogLine not found");
            return 0;
        }

        // setter 찾기: set_dialogReadPercent(float) (대소문자/네이밍 차이 대비)
        var setter = girl.Methods.FirstOrDefault(x =>
            (x.Name == "set_dialogReadPercent" || x.Name == "set_DialogReadPercent") &&
            x.Parameters.Count == 1 &&
            x.Parameters[0].ParameterType.FullName == "System.Single"
        );

        if (setter == null)
        {
            Console.WriteLine("[WARN] dialogReadPercent setter not found on Girl");
            return 0;
        }

        var il = m.Body.GetILProcessor();
        var ins = m.Body.Instructions;

        // Sequence.Play() 호출 찾기
        var playCall = ins.FirstOrDefault(i =>
            (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call) &&
            i.Operand is MethodReference mr &&
            mr.Name == "Play" &&
            mr.DeclaringType != null &&
            mr.DeclaringType.FullName != null &&
            mr.DeclaringType.FullName.Contains("Holoville.HOTween.Core.Sequence")
        );

        if (playCall == null)
        {
            Console.WriteLine("[WARN] Sequence.Play() call not found in Girl.ReadDialogLine");
            return 0;
        }

        // 중복 주입 방지: Play() 앞쪽 근처에 set_dialogReadPercent가 이미 있으면 skip
        int playIndex = ins.IndexOf(playCall);
        bool already = false;
        for (int back = 1; back <= 20; back++)
        {
            int idx = playIndex - back;
            if (idx < 0) break;

            var prev = ins[idx];
            if ((prev.OpCode == OpCodes.Call || prev.OpCode == OpCodes.Callvirt) &&
                prev.Operand is MethodReference mr2 &&
                (mr2.Name == "set_dialogReadPercent" || mr2.Name == "set_DialogReadPercent"))
            {
                already = true;
                break;
            }
        }

        if (already)
        {
            Console.WriteLine("[OK] Typewriter already disabled (setter found near Play)");
            return 0;
        }

        // Inject right before Play():
        // ldarg.0
        // ldc.r4 1.0
        // callvirt instance void Girl::set_dialogReadPercent(float32)
        il.InsertBefore(playCall, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(playCall, il.Create(OpCodes.Ldc_R4, 1.0f));
        il.InsertBefore(playCall, il.Create(OpCodes.Callvirt, mod.ImportReference(setter)));

        Console.WriteLine("[OK] Patched Girl.ReadDialogLine: force dialogReadPercent=1 before Play()");
        return 1;
    }

    static bool AlreadyPatched(MethodDefinition m)
    {
        try
        {
            return m.Body.Instructions.Take(16).Any(ins =>
                (ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) &&
                ins.Operand is MethodReference mr &&
                mr.Name == "OnSetText" &&
                mr.DeclaringType != null &&
                mr.DeclaringType.Name == "KRHook"
            );
        }
        catch
        {
            return false;
        }
    }
}
